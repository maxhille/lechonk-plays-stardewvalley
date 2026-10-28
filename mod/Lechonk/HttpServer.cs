using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using StardewModdingAPI;

namespace Lechonk;

public class HttpServer
{
    private static readonly UTF8Encoding NoBomUtf8 = new(false);

    private readonly TcpListener _listener;
    private readonly ScriptController _controller;
    private readonly BotApi _botApi;
    private readonly IMonitor _monitor;
    private bool _stop;

    public bool IsRunning => !_stop;

    public HttpServer(ScriptController controller, BotApi botApi, IMonitor monitor)
    {
        _controller = controller;
        _botApi = botApi;
        _monitor = monitor;
        _listener = new TcpListener(IPAddress.Loopback, 17642);
    }

    public void Start()
    {
        _listener.Start();
        var thread = new Thread(RunLoop) { Name = "lechonk-http", IsBackground = true };
        thread.Start();
        _monitor.Log($"[Lechonk] HTTP server listening on 127.0.0.1:17642", LogLevel.Info);
    }

    public void Stop()
    {
        _stop = true;
        _listener.Stop();
    }

    private void RunLoop()
    {
        while (!_stop)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_stop)
                    _monitor.Log($"[Lechonk] AcceptTcpClient failed: {ex.Message}", LogLevel.Warn);
                Thread.Sleep(1000);
                continue;
            }

            var handler = new Thread(() => HandleClient(client))
            {
                Name = "lechonk-http-handler",
                IsBackground = true
            };
            handler.Start();
        }
    }

    /// <summary>Maximum request body size in bytes — keeps scripts small.</summary>
    public const int MaxBodyBytes = 1024;

    private void HandleClient(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buf = new byte[8192];
            var head = new MemoryStream();
            var body = new MemoryStream();

            // Read until the headers are complete (CRLF CRLF). Body bytes that
            // arrive in the same chunks are carried over into `body`.
            // Byte-level on purpose: a buffered character reader can hand back
            // a partial body and report end-of-stream early.
            int contentLength = 0;
            string contentType = "";
            string method = "", path = "";
            bool headersDone = false;
            while (!headersDone)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return;
                head.Write(buf, 0, n);
                int end = HeaderEnd(head.GetBuffer(), (int)head.Length);
                if (end < 0)
                {
                    if (head.Length > 8192) return; // oversized header — bail
                    continue;
                }
                var all = head.ToArray();
                var headerText = NoBomUtf8.GetString(all, 0, end);
                var lines = headerText.Split('\n');
                var requestLine = lines[0].TrimEnd('\r').Split(' ');
                if (requestLine.Length >= 2)
                {
                    method = requestLine[0];
                    path = requestLine[1].Split('?')[0];
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.StartsWith("Content-Length:"))
                        contentLength = int.Parse(line.Substring(15).Trim());
                    else if (line.StartsWith("Content-Type:"))
                        contentType = line.Substring(13).Trim();
                }
                int extra = all.Length - end - 4;
                if (extra > 0) body.Write(all, end + 4, extra);
                headersDone = true;
            }

            if (method == "GET" && path == "/api")
            {
                HandleApi(client);
                return;
            }
            if (method == "POST" && path == "/pause")
            {
                // Admin toggle (not exposed to the model): opens/closes the
                // in-game menu (GameMenu) directly on the game thread — no
                // input, so it works even when the game window has lost focus.
                // While open, the bot's script is frozen.
                PauseResponse r;
                try
                {
                    r = _controller.TogglePauseBlocking();
                }
                catch (Exception ex)
                {
                    r = new PauseResponse { Ok = false, Error = ex.Message };
                }
                string json = JsonSerializer.Serialize(new { ok = r.Ok, paused = r.Paused, error = r.Error });
                SendResponse(client, r.Ok ? 200 : 400, json);
                return;
            }
            if (method == "POST" && path == "/script")
            {
                HandleScript(client, stream, contentLength, contentType, body);
                return;
            }

            SendResponse(client, 404, "{}");
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Lechonk] HandleClient error: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            client.Close();
        }
    }

    /// <summary>Index of the CRLF CRLF header terminator in data[0..len), or -1.</summary>
    private static int HeaderEnd(byte[] data, int len)
    {
        for (int i = len - 4; i >= 0; i--)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }

    private void HandleScript(TcpClient client, Stream stream, int contentLength, string contentType, MemoryStream body)
    {
        if (contentLength > MaxBodyBytes)
        {
            SendResponse(client, 413, "{\"error\":\"body exceeds the " + MaxBodyBytes + "-byte limit\"}");
            return;
        }

        // Read exactly contentLength body bytes (some may already be in `body`).
        var buf = new byte[8192];
        while (body.Length < contentLength)
        {
            int n = stream.Read(buf, 0, buf.Length);
            if (n <= 0) break; // client vanished mid-body
            body.Write(buf, 0, n);
        }

        string bodyStr = body.Length > 0
            ? NoBomUtf8.GetString(body.GetBuffer(), 0, (int)Math.Min(body.Length, contentLength))
            : "";

        string scriptCode;
        if (!string.IsNullOrEmpty(contentType) && contentType.Contains("application/json"))
        {
            // JSON envelope: {"script": "<lua code>"}
            try
            {
                using var doc = JsonDocument.Parse(bodyStr);
                scriptCode = doc.RootElement.TryGetProperty("script", out var el) ? (el.GetString() ?? "") : bodyStr;
            }
            catch
            {
                scriptCode = bodyStr.Trim();
            }
        }
        else
        {
            // application/lua or plain text: body is the Lua script as-is.
            scriptCode = bodyStr.Trim();
        }

        if (string.IsNullOrEmpty(scriptCode))
        {
            SendResponse(client, 400, "{\"error\":\"empty script\"}");
            return;
        }

        var result = _controller.RunScriptBlocking(scriptCode, 20000);

        string json;
        try
        {
            json = JsonSerializer.Serialize(new
            {
                result = result.Result,
                error = result.Error,
                ticksUsed = result.TicksUsed,
                evt = result.EventName
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            json = "{\"error\":\"serialization failed\"}";
        }

        SendResponse(client, 200, json);
    }

    private void SendResponse(TcpClient client, int status, string json)
    {
        var stream = client.GetStream();
        using var writer = new StreamWriter(stream, NoBomUtf8) { AutoFlush = true };
        var reason = status == 200 ? "OK" : (status == 400 ? "Bad Request" : (status == 404 ? "Not Found" : (status == 413 ? "Payload Too Large" : "Error")));
        writer.Write($"HTTP/1.1 {status} {reason}\r\n");
        writer.Write("Content-Type: application/json\r\n");
        writer.Write($"Content-Length: {json.Length}\r\n\r\n");
        writer.Write(json);
        writer.Flush();
        writer.Close();
    }

    private void HandleApi(TcpClient client)
    {
        var schema = _botApi.GetApiSchema();
        var json = JsonSerializer.Serialize(schema);
        SendResponse(client, 200, json);
    }
}
