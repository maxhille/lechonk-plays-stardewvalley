using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using StardewModdingAPI;

namespace Lechonk;

/// <summary>Minimal HTTP server on localhost:17642 using raw TcpListener.</summary>
public class HttpServer
{
    private readonly TcpListener _listener;
    private readonly ScriptController _controller;
    private readonly BotApi _botApi;
    private readonly IMonitor _monitor;
    private readonly Thread _serverThread;
    private volatile bool _running;

    public string Address => "http://127.0.0.1:17642/";
    public int Port => 17642;

    public HttpServer(ScriptController controller, BotApi botApi, IMonitor monitor)
    {
        _controller = controller;
        _botApi = botApi;
        _monitor = monitor;

        _listener = new TcpListener(IPAddress.Loopback, Port);
        _running = false;
        _serverThread = new Thread(RunLoop) { Name = "Lechonk-HTTP", IsBackground = true };
    }

    public void Run()
    {
        _listener.Start();
        _running = true;
        _serverThread.Start();
        _monitor.Log($"Lechonk HTTP server listening on {Address.TrimEnd('/')}");
    }

    private void RunLoop()
    {
        while (_running)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                var t = new Thread(() => HandleClient(client)) { Name = "Lechonk-HTTP-Handler", IsBackground = true };
                t.Start();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                try { _monitor.Log($"HTTP accept error: {ex.Message}", LogLevel.Error); } catch { }
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];

            // Read whatever's available in one shot
            var totalRead = stream.Read(buffer, 0, buffer.Length);
            if (totalRead == 0) return;

            var requestText = Encoding.UTF8.GetString(buffer, 0, totalRead);

            // Parse request line
            var firstLineEnd = requestText.IndexOf("\r\n");
            if (firstLineEnd < 0) { client.Close(); return; }

            var requestLine = requestText.Substring(0, firstLineEnd);
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) { client.Close(); return; }

            var method = parts[0];
            var fullPath = parts[1];
            var path = fullPath.Contains('?') ? fullPath.Split('?')[0] : fullPath;
            var queryString = fullPath.Contains('?') ? fullPath.Substring(fullPath.IndexOf('?') + 1) : "";

            // Parse headers to find Content-Length
            int contentLength = 0;
            var headersEnd = requestText.IndexOf("\r\n\r\n");
            if (headersEnd > 0)
            {
                var headerSection = requestText.Substring(0, headersEnd);
                var headerLines = headerSection.Split(new[] { "\r\n" }, StringSplitOptions.None);
                for (int i = 1; i < headerLines.Length; i++)
                {
                    if (headerLines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(headerLines[i].Split(':')[1].Trim(), out contentLength);
                    }
                }
            }

            // Extract body
            string body = "";
            if (method == "POST" && headersEnd > 0 && headersEnd + 4 < totalRead)
            {
                body = Encoding.UTF8.GetString(buffer, headersEnd + 4, totalRead - headersEnd - 4);
            }

            // Route
            if (path == "/script" && method == "POST")
            {
                HandleScript(client, body);
            }
            else if (path == "/api" && method == "GET")
            {
                HandleApi(client);
            }
            else if (path == "/status" && method == "GET")
            {
                HandleStatus(client, queryString);
            }
            else if (path == "/debug" && method == "GET")
            {
                HandleDebug(client);
            }
            else
            {
                SendRaw(client, "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\nnot found");
            }
        }
        catch (Exception ex)
        {
            try { _monitor.Log($"HTTP request error: {ex.Message}", LogLevel.Error); } catch { }
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private void SendRaw(TcpClient client, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var stream = client.GetStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private void WriteJson(TcpClient client, string json, int statusCode)
    {
        var header = $"HTTP/1.1 {statusCode} OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n";
        SendRaw(client, header + json);
    }

    private void HandleScript(TcpClient client, string body)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ScriptRequest>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (request == null || string.IsNullOrWhiteSpace(request.Script))
            {
                WriteJson(client, JsonSerializer.Serialize(new { error = "missing script" }, new JsonSerializerOptions { WriteIndented = false }), 400);
                return;
            }

            int tickBudget = request.TickBudget ?? 2000;
            var scriptId = _controller.RunScript(request.Script, tickBudget);

            if (scriptId < 0)
            {
                WriteJson(client, JsonSerializer.Serialize(new { error = "script compilation failed" }, new JsonSerializerOptions { WriteIndented = false }), 400);
                return;
            }

            WriteJson(client, JsonSerializer.Serialize(new { scriptId }, new JsonSerializerOptions { WriteIndented = false }), 202);
        }
        catch (Exception ex)
        {
            WriteJson(client, JsonSerializer.Serialize(new { error = ex.Message }, new JsonSerializerOptions { WriteIndented = false }), 500);
        }
    }

    private void HandleStatus(TcpClient client, string queryString)
    {
        var scriptIdStr = "";
        if (!string.IsNullOrEmpty(queryString))
        {
            var pairs = queryString.Split('&');
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=');
                if (kv.Length == 2 && kv[0] == "scriptId")
                    scriptIdStr = kv[1];
            }
        }

        if (string.IsNullOrEmpty(scriptIdStr) || !int.TryParse(scriptIdStr, out var scriptId))
        {
            WriteJson(client, JsonSerializer.Serialize(new { error = "missing scriptId query param" }, new JsonSerializerOptions { WriteIndented = false }), 400);
            return;
        }

        var response = _controller.GetResult(scriptId);
        if (response != null)
        {
            WriteJson(client, JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false }), 200);
        }
        else
        {
            WriteJson(client, JsonSerializer.Serialize(new { running = true }, new JsonSerializerOptions { WriteIndented = false }), 204);
        }
    }

    private void HandleApi(TcpClient client)
    {
        var schema = _botApi.GetApiSchema();
        WriteJson(client, JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true }), 200);
    }

    private void HandleDebug(TcpClient client)
    {
        var active = _controller.IsScriptActive();
        var result = _controller.GetResult(1);
        WriteJson(client, JsonSerializer.Serialize(new { active, result }, new JsonSerializerOptions { WriteIndented = true }), 200);
    }

    /// <summary>Stop the server.</summary>
    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        try { _serverThread.Join(1000); } catch { }
    }
}
