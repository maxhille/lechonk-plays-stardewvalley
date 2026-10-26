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

    private void HandleClient(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();

            // Read the request line
            var reader = new StreamReader(stream, NoBomUtf8, false, 128, true);
            string? requestLine = reader.ReadLine();
            if (requestLine == null) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0];
            string path = parts[1].Split('?')[0];

            if (method == "GET" && path == "/api")
            {
                HandleApi(client);
                return;
            }
            if (method == "POST" && path == "/script")
            {
                HandleScript(client, reader);
                return;
            }

            string body404 = "{}";
            var writer = new StreamWriter(stream, NoBomUtf8) { AutoFlush = true };
            writer.Write($"HTTP/1.1 404 Not Found\r\n");
            writer.Write("Content-Type: application/json\r\n");
            writer.Write($"Content-Length: {body404.Length}\r\n\r\n");
            writer.Write(body404);
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

    private void HandleScript(TcpClient client, StreamReader reader)
    {
        int contentLength = 0;
        string contentType = null;
        string? line;
        while ((line = reader.ReadLine()) != null && line.Length > 0)
        {
            if (line.StartsWith("Content-Length:"))
            {
                contentLength = int.Parse(line.Substring(15).Trim());
            }
            else if (line.StartsWith("Content-Type:"))
            {
                contentType = line.Substring(13).Trim();
            }
        }

        string body;
        if (contentLength > 0)
        {
            var buffer = new char[contentLength];
            int total = 0;
            while (total < contentLength)
            {
                int n = reader.Read(buffer, total, contentLength - total);
                if (n <= 0) break;
                total += n;
            }
            body = new string(buffer, 0, total);
        }
        else
        {
            body = reader.ReadToEnd();
        }

        string scriptCode;
        if (!string.IsNullOrEmpty(contentType) && contentType.Contains("application/json"))
        {
            // JSON envelope: {"script": "<lua code>"}
            try
            {
                using var doc = JsonDocument.Parse(body);
                scriptCode = doc.RootElement.TryGetProperty("script", out var el) ? (el.GetString() ?? "") : body;
            }
            catch
            {
                scriptCode = body.Trim();
            }
        }
        else
        {
            // application/lua or plain text: body is the Lua script as-is.
            scriptCode = body.Trim();
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
        var reason = status == 200 ? "OK" : (status == 400 ? "Bad Request" : "Not Found");
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
