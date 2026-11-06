using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GtViewer.Poc;

public class MeshCentralPocClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _serverUri;
    private readonly string _username;
    private readonly string _password;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoopTask;

    public event Action<string>? OnEventReceived;
    public event Action<string, JsonNode?>? OnNodeConnect;

    public MeshCentralPocClient(Uri serverUri, string username, string password)
    {
        _serverUri = serverUri;
        _username = username;
        _password = password;

        // Trust local self-signed dev certificates if connecting via HTTPS/WSS to localhost
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(_username)) + "," +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(_password));
        _ws.Options.SetRequestHeader("x-meshauth", authValue);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[WebSocket] Conectando a {_serverUri} como usuário '{_username}'...");
        await _ws.ConnectAsync(_serverUri, cancellationToken);
        Console.WriteLine("[WebSocket] Conexão estabelecida com sucesso!");

        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendActionAsync(object payload, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private readonly TaskCompletionSource<JsonNode?> _serverInfoTcs = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pendingResponses = new();

    public async Task<JsonNode?> GetServerInfoAsync(TimeSpan timeout)
    {
        await SendActionAsync(new { action = "serverinfo" });
        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => _serverInfoTcs.TrySetCanceled());
        return await _serverInfoTcs.Task;
    }

    public async Task<JsonNode?> GetNodesAsync(TimeSpan timeout)
    {
        var responseId = "gtv_nodes_" + Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<JsonNode?>();
        lock (_pendingResponses)
        {
            _pendingResponses[responseId] = tcs;
        }

        await SendActionAsync(new { action = "nodes", responseid = responseId });

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    public async Task<JsonNode?> GetSysInfoAsync(string nodeId, TimeSpan timeout)
    {
        var responseId = "gtv_sysinfo_" + Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<JsonNode?>();
        lock (_pendingResponses)
        {
            _pendingResponses[responseId] = tcs;
        }

        await SendActionAsync(new
        {
            action = "getsysinfo",
            nodeid = nodeId,
            nodeinfo = true,
            responseid = responseId
        });

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    public async Task<RunCommandResult> RunFixedScriptAsync(string nodeId, string script, TimeSpan timeout)
    {
        var responseId = "gtv_cmd_" + Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<JsonNode?>();
        lock (_pendingResponses)
        {
            _pendingResponses[responseId] = tcs;
        }

        // type 2 = PowerShell, runAsUser: 0 = SYSTEM, reply: true = return stdout/stderr
        await SendActionAsync(new
        {
            action = "runcommands",
            nodeids = new[] { nodeId },
            type = 2,
            cmds = script,
            runAsUser = 0,
            reply = true,
            responseid = responseId
        });

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());
        var responseNode = await tcs.Task;

        var rawOutput = responseNode?["result"]?.ToString() ?? "";
        return ParseRunCommandResult(rawOutput);
    }

    public static RunCommandResult ParseRunCommandResult(string output)
    {
        var resultMatch = Regex.Match(output, @"GTV-RESULT=([^\r\n]+)");
        var exitMatch = Regex.Match(output, @"GTV-EXIT=(\d+)");

        var gtvResult = resultMatch.Success ? resultMatch.Groups[1].Value.Trim() : "DESCONHECIDO";
        var exitCode = exitMatch.Success && int.TryParse(exitMatch.Groups[1].Value, out var code) ? code : -1;

        return new RunCommandResult(gtvResult, exitCode, output);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();

        try
        {
            while (!_cts.Token.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var message = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    ProcessMessage(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] Erro na recepção: {ex.Message}");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return;

            var action = node["action"]?.ToString();
            var responseId = node["responseid"]?.ToString();

            // Serverinfo
            if (action == "serverinfo")
            {
                _serverInfoTcs.TrySetResult(node["serverinfo"]);
            }

            // Pending response lookup
            if (!string.IsNullOrEmpty(responseId))
            {
                lock (_pendingResponses)
                {
                    if (_pendingResponses.Remove(responseId, out var tcs))
                    {
                        tcs.TrySetResult(node);
                    }
                }
            }

            // Also check action == "msg" and type == "runcommands"
            if (action == "msg" && node["type"]?.ToString() == "runcommands")
            {
                var cmdResponseId = node["responseid"]?.ToString();
                if (!string.IsNullOrEmpty(cmdResponseId))
                {
                    lock (_pendingResponses)
                    {
                        if (_pendingResponses.Remove(cmdResponseId, out var tcs))
                        {
                            tcs.TrySetResult(node);
                        }
                    }
                }
            }

            // Events: nodeconnect / changenode
            if (action == "event")
            {
                var evt = node["event"];
                var evtAction = evt?["action"]?.ToString();
                var nodeId = evt?["nodeid"]?.ToString();
                OnEventReceived?.Invoke(json);

                if (evtAction == "nodeconnect" && !string.IsNullOrEmpty(nodeId))
                {
                    Console.WriteLine($"\n[EVENTO] Máquina conectada! NodeId: {nodeId}");
                    OnNodeConnect?.Invoke(nodeId, evt);
                }
            }
            else if (action == "nodeconnect")
            {
                var nodeId = node["nodeid"]?.ToString() ?? node["node"]?["_id"]?.ToString();
                if (!string.IsNullOrEmpty(nodeId))
                {
                    Console.WriteLine($"\n[EVENTO] nodeconnect direto! NodeId: {nodeId}");
                    OnNodeConnect?.Invoke(nodeId, node);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] Falha ao processar mensagem JSON: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch
        {
            // Ignore
        }
        _ws.Dispose();
        _cts.Dispose();
    }
}

public record RunCommandResult(string GtvResult, int ExitCode, string RawOutput);
