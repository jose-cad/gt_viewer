using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GtViewer.Web.Models;
using GtViewer.Web.Models.MeshCentral;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GtViewer.Web.Services;

public class MeshCentralClient : IMeshCentralClient, IHostedService
{
    private readonly MeshCentralOptions _options;
    private readonly ILogger<MeshCentralClient> _logger;
    private readonly Func<IMeshWebSocketConnection> _connectionFactory;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pendingRequests = new();
    private readonly List<TaskCompletionSource<JsonNode?>> _serverInfoWaiters = new();

    private CancellationTokenSource _cts = new();
    private Task? _connectionLoopTask;
    private TaskCompletionSource<bool>? _initialConnectTcs;
    private IMeshWebSocketConnection? _currentConnection;
    private volatile bool _isConnected;
    private volatile bool _disposed;
    private JsonNode? _cachedServerInfo;
    private IReadOnlyDictionary<string, JsonNode> _cachedNodes = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

    public bool IsConnected => _isConnected;
    public IReadOnlyDictionary<string, JsonNode> CachedNodes => _cachedNodes;

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<MeshNodeEventArgs>? NodeConnected;
    public event EventHandler<MeshNodeEventArgs>? NodeDisconnected;
    public event EventHandler<MeshNodeEventArgs>? NodeChanged;
    public event EventHandler<FullSyncEventArgs>? FullSyncCompleted;
    public event EventHandler<string>? RawMessageReceived;

    public MeshCentralClient(
        IOptions<MeshCentralOptions> options,
        ILogger<MeshCentralClient> logger,
        Func<IMeshWebSocketConnection>? connectionFactory = null)
    {
        _options = options.Value;
        _logger = logger;
        _connectionFactory = connectionFactory ?? (() => new DefaultMeshWebSocketConnection());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.AutoConnect)
        {
            _logger.LogInformation("Iniciando rotina de conexão persistente com o MeshCentral em {Url}...", _options.WebSocketUrl);
            _connectionLoopTask = Task.Run(RunPersistentConnectionLoopAsync);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Parando cliente MeshCentral...");
        await DisconnectAsync(cancellationToken);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? waitTcs = null;

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_isConnected) return;

            if (_connectionLoopTask == null || _connectionLoopTask.IsCompleted)
            {
                if (_cts.IsCancellationRequested)
                {
                    _cts = new CancellationTokenSource();
                }
                _initialConnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTcs = _initialConnectTcs;
                _connectionLoopTask = Task.Run(RunPersistentConnectionLoopAsync);
            }
            else if (_initialConnectTcs != null && !_initialConnectTcs.Task.IsCompleted)
            {
                waitTcs = _initialConnectTcs;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (waitTcs != null)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using (timeoutCts.Token.Register(() => waitTcs.TrySetCanceled()))
            {
                await waitTcs.Task;
            }
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        try
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            if (_currentConnection != null)
            {
                try
                {
                    await _currentConnection.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", cancellationToken);
                }
                catch
                {
                    // Ignora erros ao fechar socket em desconexão
                }
            }

            SetDisconnected();

            if (_connectionLoopTask != null)
            {
                try
                {
                    await _connectionLoopTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch
                {
                    // Timeout ou cancelamento ao aguardar encerramento do loop
                }
                _connectionLoopTask = null;
            }
        }
        finally
        {
            try
            {
                _lifecycleLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task RunPersistentConnectionLoopAsync()
    {
        var targetUri = new Uri(_options.WebSocketUrl);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("[MeshCentralClient] Conectando a {Uri} como usuário '{User}'...", targetUri, _options.ServiceUsername);
                _currentConnection = _connectionFactory();

                await _currentConnection.ConnectAsync(
                    targetUri,
                    _options.ServiceUsername,
                    _options.ServicePassword,
                    _options.IgnoreSslErrors,
                    _cts.Token);

                _isConnected = true;
                _logger.LogInformation("[MeshCentralClient] Conexão WebSocket estabelecida com sucesso!");
                _initialConnectTcs?.TrySetResult(true);
                ConnectionStateChanged?.Invoke(this, true);

                // Dispara sincronização completa imediata (Invariante spec.md:239)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SynchronizeNodesInternalAsync(_cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MeshCentralClient] Falha na sincronização completa inicial/reconexão.");
                    }
                });

                // Loop de escuta contínua de mensagens
                await ReceiveLoopAsync(_currentConnection, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MeshCentralClient] Desconexão ou falha de rede no MeshCentral: {Message}", ex.Message);
                _initialConnectTcs?.TrySetException(ex);
            }
            finally
            {
                SetDisconnected();
                if (_currentConnection != null)
                {
                    try { await _currentConnection.DisposeAsync(); } catch { }
                    _currentConnection = null;
                }
            }

            if (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("[MeshCentralClient] Aguardando {Seconds}s para tentar reconectar...", _options.ReconnectIntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectIntervalSeconds), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void SetDisconnected()
    {
        var wasConnected = _isConnected;
        _isConnected = false;
        _cachedServerInfo = null;

        var disconnectedEx = new MeshCentralDisconnectedException("A conexão com o servidor MeshCentral foi perdida.");
        _initialConnectTcs?.TrySetException(disconnectedEx);

        lock (_serverInfoWaiters)
        {
            foreach (var tcs in _serverInfoWaiters)
            {
                tcs.TrySetException(disconnectedEx);
            }
            _serverInfoWaiters.Clear();
        }

        lock (_pendingRequests)
        {
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetException(disconnectedEx);
            }
            _pendingRequests.Clear();
        }

        if (wasConnected)
        {
            _logger.LogWarning("[MeshCentralClient] Estado alterado: DESCONECTADO.");
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    private async Task ReceiveLoopAsync(IMeshWebSocketConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested && connection.State == WebSocketState.Open)
        {
            var result = await connection.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation("[MeshCentralClient] Servidor solicitou fechamento da conexão WebSocket.");
                break;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);
                ProcessIncomingMessage(json);
            }
        }
    }

    private void ProcessIncomingMessage(string json)
    {
        try
        {
            RawMessageReceived?.Invoke(this, json);

            var node = JsonNode.Parse(json);
            if (node == null) return;

            var action = node["action"]?.ToString();
            var responseId = node["responseid"]?.ToString() ?? node["tag"]?.ToString();

            // 1. Tratamento de serverinfo
            if (action == "serverinfo")
            {
                _cachedServerInfo = node["serverinfo"] ?? node;
                lock (_serverInfoWaiters)
                {
                    foreach (var waiter in _serverInfoWaiters)
                    {
                        waiter.TrySetResult(_cachedServerInfo);
                    }
                    _serverInfoWaiters.Clear();
                }
            }

            // 2. Resposta correlacionada por responseid / tag
            if (!string.IsNullOrEmpty(responseId))
            {
                ResolvePendingRequest(responseId, node);
            }

            // 3. Resposta de execução de comandos (action == "msg" e type == "runcommands")
            if (action == "msg" && node["type"]?.ToString() == "runcommands")
            {
                var cmdResponseId = node["responseid"]?.ToString() ?? node["tag"]?.ToString();
                if (!string.IsNullOrEmpty(cmdResponseId))
                {
                    ResolvePendingRequest(cmdResponseId, node);
                }
            }

            // 4. Despacho de eventos em tempo real
            DispatchEvents(action, node);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MeshCentralClient] Falha ao processar mensagem JSON: {Message}", ex.Message);
        }
    }

    private void ResolvePendingRequest(string responseId, JsonNode node)
    {
        lock (_pendingRequests)
        {
            if (_pendingRequests.Remove(responseId, out var tcs))
            {
                tcs.TrySetResult(node);
            }
        }
    }

    private void DispatchEvents(string? action, JsonNode node)
    {
        if (action == "event")
        {
            var evt = node["event"];
            var evtAction = evt?["action"]?.ToString();
            var nodeId = evt?["nodeid"]?.ToString() ?? evt?["node"]?["_id"]?.ToString();

            if (!string.IsNullOrEmpty(nodeId))
            {
                if (evtAction == "nodeconnect")
                {
                    NodeConnected?.Invoke(this, new MeshNodeEventArgs(nodeId, evt));
                }
                else if (evtAction == "nodedisconnect")
                {
                    NodeDisconnected?.Invoke(this, new MeshNodeEventArgs(nodeId, evt));
                }
                else if (evtAction == "changenode")
                {
                    NodeChanged?.Invoke(this, new MeshNodeEventArgs(nodeId, evt));
                }
            }
        }
        else if (action == "nodeconnect")
        {
            var nodeId = node["nodeid"]?.ToString() ?? node["node"]?["_id"]?.ToString();
            if (!string.IsNullOrEmpty(nodeId))
            {
                NodeConnected?.Invoke(this, new MeshNodeEventArgs(nodeId, node));
            }
        }
        else if (action == "nodedisconnect")
        {
            var nodeId = node["nodeid"]?.ToString() ?? node["node"]?["_id"]?.ToString();
            if (!string.IsNullOrEmpty(nodeId))
            {
                NodeDisconnected?.Invoke(this, new MeshNodeEventArgs(nodeId, node));
            }
        }
        else if (action == "changenode")
        {
            var nodeId = node["nodeid"]?.ToString() ?? node["node"]?["_id"]?.ToString();
            if (!string.IsNullOrEmpty(nodeId))
            {
                NodeChanged?.Invoke(this, new MeshNodeEventArgs(nodeId, node));
            }
        }
    }

    private async Task SendActionRawAsync(object payload, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _currentConnection == null)
        {
            throw new MeshCentralDisconnectedException("O cliente MeshCentral não está conectado.");
        }

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected || _currentConnection == null)
            {
                throw new MeshCentralDisconnectedException("A conexão caiu antes do envio da mensagem.");
            }
            await _currentConnection.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<JsonNode?> SendRequestAsync(
        object payload,
        string? responseId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _currentConnection == null)
        {
            throw new MeshCentralDisconnectedException("O cliente MeshCentral não está conectado.");
        }

        responseId ??= "gtv_" + Guid.NewGuid().ToString("N")[..12];

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingRequests)
        {
            _pendingRequests[responseId] = tcs;
        }

        var jsonNode = JsonSerializer.SerializeToNode(payload) ?? new JsonObject();
        jsonNode["responseid"] = responseId;
        jsonNode["tag"] = responseId;

        var jsonBytes = Encoding.UTF8.GetBytes(jsonNode.ToJsonString());

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
        linkedCts.CancelAfter(effectiveTimeout);

        try
        {
            await _sendLock.WaitAsync(linkedCts.Token);
            try
            {
                if (!IsConnected || _currentConnection == null)
                {
                    throw new MeshCentralDisconnectedException("A conexão caiu antes do envio da requisição.");
                }

                await _currentConnection.SendAsync(
                    new ArraySegment<byte>(jsonBytes),
                    WebSocketMessageType.Text,
                    true,
                    linkedCts.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            linkedCts.Token.Register(() =>
            {
                lock (_pendingRequests)
                {
                    if (_pendingRequests.Remove(responseId, out var pendingTcs))
                    {
                        pendingTcs.TrySetException(new MeshCentralTimeoutException(
                            $"Tempo limite de {effectiveTimeout.TotalSeconds}s esgotado aguardando resposta para '{responseId}'."));
                    }
                }
            });

            return await tcs.Task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_pendingRequests) { _pendingRequests.Remove(responseId); }
            throw;
        }
        catch (OperationCanceledException)
        {
            lock (_pendingRequests) { _pendingRequests.Remove(responseId); }
            throw new MeshCentralTimeoutException($"Tempo limite de {effectiveTimeout.TotalSeconds}s esgotado aguardando resposta para '{responseId}'.");
        }
        catch
        {
            lock (_pendingRequests) { _pendingRequests.Remove(responseId); }
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, JsonNode>> SynchronizeNodesInternalAsync(CancellationToken cancellationToken)
    {
        var nodes = await GetNodesAsync(TimeSpan.FromSeconds(10), cancellationToken);
        _cachedNodes = nodes;
        FullSyncCompleted?.Invoke(this, new FullSyncEventArgs(nodes));
        return nodes;
    }

    public async Task<JsonNode?> GetServerInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MeshCentralDisconnectedException("O cliente MeshCentral não está conectado.");
        }

        if (_cachedServerInfo != null)
        {
            return _cachedServerInfo;
        }

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_serverInfoWaiters)
        {
            _serverInfoWaiters.Add(tcs);
        }

        await SendActionRawAsync(new { action = "serverinfo" }, cancellationToken);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(effectiveTimeout);

        linkedCts.Token.Register(() =>
        {
            lock (_serverInfoWaiters)
            {
                _serverInfoWaiters.Remove(tcs);
            }
            tcs.TrySetException(new MeshCentralTimeoutException($"Tempo limite de {effectiveTimeout.TotalSeconds}s esgotado aguardando serverinfo."));
        });

        return await tcs.Task;
    }

    public async Task<IReadOnlyDictionary<string, JsonNode>> GetNodesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new { action = "nodes" }, timeout: timeout, cancellationToken: cancellationToken);
        var result = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

        var nodesProp = response?["nodes"];
        if (nodesProp is JsonObject obj)
        {
            foreach (var meshProp in obj)
            {
                if (meshProp.Value is JsonObject meshNodes)
                {
                    foreach (var nodeProp in meshNodes)
                    {
                        if (nodeProp.Value != null)
                        {
                            var id = nodeProp.Value["_id"]?.ToString() ?? nodeProp.Key;
                            result[id] = nodeProp.Value;
                        }
                    }
                }
                else if (meshProp.Value != null)
                {
                    var id = meshProp.Value["_id"]?.ToString() ?? meshProp.Key;
                    result[id] = meshProp.Value;
                }
            }
        }
        else if (nodesProp is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item != null)
                {
                    var id = item["_id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        result[id] = item;
                    }
                }
            }
        }

        _cachedNodes = result;
        return result;
    }

    public async Task<JsonNode?> GetSysInfoAsync(string nodeId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(new
        {
            action = "getsysinfo",
            nodeid = nodeId,
            nodeinfo = true
        }, timeout: timeout, cancellationToken: cancellationToken);
    }

    public async Task<RunCommandResult> RunFixedScriptAsync(string nodeId, string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(new
        {
            action = "runcommands",
            nodeids = new[] { nodeId },
            type = 2, // PowerShell
            cmds = script,
            runAsUser = 0, // SYSTEM
            reply = true
        }, timeout: timeout, cancellationToken: cancellationToken);

        var rawOutput = response?["result"]?.ToString() ?? "";
        return ParseRunCommandResult(rawOutput);
    }

    public async Task<string> EnsureMirrorUserAsync(string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var mirrorUsername = technicianUsername.StartsWith("gtv_mirror_")
            ? technicianUsername
            : $"gtv_mirror_{technicianUsername}";

        var res = await SendRequestAsync(new { action = "users" }, timeout: timeout, cancellationToken: cancellationToken);
        var users = res?["users"]?.AsArray();
        var exists = false;

        if (users != null)
        {
            foreach (var u in users)
            {
                var name = u?["name"]?.ToString();
                if (string.Equals(name, mirrorUsername, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await SendRequestAsync(new
            {
                action = "adduser",
                username = mirrorUsername,
                email = $"{mirrorUsername}@local",
                siteadmin = 0
            }, timeout: timeout, cancellationToken: cancellationToken);
        }

        return mirrorUsername;
    }

    public async Task<bool> GrantRemoteDesktopRightsAsync(string nodeId, string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var mirrorUsername = technicianUsername.StartsWith("gtv_mirror_")
            ? technicianUsername
            : $"gtv_mirror_{technicianUsername}";

        var res = await SendRequestAsync(new
        {
            action = "adddeviceuser",
            nodeid = nodeId,
            usernames = new[] { mirrorUsername },
            rights = 8 // MESHRIGHT_REMOTECONTROL (apenas tela, sem console, sem arquivos)
        }, timeout: timeout, cancellationToken: cancellationToken);

        var resultText = res?["result"]?.ToString() ?? res?["value"]?.ToString();
        return resultText == null || !resultText.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> RevokeRemoteDesktopRightsAsync(string nodeId, string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var mirrorUsername = technicianUsername.StartsWith("gtv_mirror_")
            ? technicianUsername
            : $"gtv_mirror_{technicianUsername}";

        var res = await SendRequestAsync(new
        {
            action = "adddeviceuser",
            nodeid = nodeId,
            usernames = new[] { mirrorUsername },
            remove = true
        }, timeout: timeout, cancellationToken: cancellationToken);

        var resultText = res?["result"]?.ToString() ?? res?["value"]?.ToString();
        return resultText == null || !resultText.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DropUserSessionsAsync(string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var mirrorUsername = technicianUsername.StartsWith("gtv_mirror_")
            ? technicianUsername
            : $"gtv_mirror_{technicianUsername}";

        var res = await SendRequestAsync(new
        {
            action = "serverconsole",
            value = $"closeusersessions {mirrorUsername}"
        }, timeout: timeout, cancellationToken: cancellationToken);

        var resultText = res?["result"]?.ToString() ?? res?["value"]?.ToString();
        return resultText == null || !resultText.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> SendToastAsync(string nodeId, string title, string message, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var res = await SendRequestAsync(new
        {
            action = "toast",
            nodeids = new[] { nodeId },
            title = title,
            msg = message
        }, timeout: timeout, cancellationToken: cancellationToken);

        return res != null;
    }

    public async Task<RunCommandResult> RequestRestartAsync(string nodeId, TimeSpan? delay = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MeshCentralDisconnectedException("O servidor MeshCentral está desconectado. Ação de reinício recusada.");
        }

        var restartDelay = delay ?? TimeSpan.FromMinutes(2);
        var seconds = (int)restartDelay.TotalSeconds;
        var script = $"shutdown.exe /r /t {seconds} /c \"O computador será reiniciado pela TI em {seconds} segundos para manutenção.\"; Write-Output 'GTV-RESULT=OK'; Write-Output 'GTV-EXIT=0'";

        return await RunFixedScriptAsync(nodeId, script, timeout, cancellationToken);
    }

    public static RunCommandResult ParseRunCommandResult(string output)
    {
        var resultMatch = Regex.Match(output, @"GTV-RESULT=([^\r\n]+)");
        var exitMatch = Regex.Match(output, @"GTV-EXIT=(\d+)");

        var gtvResult = resultMatch.Success ? resultMatch.Groups[1].Value.Trim() : "DESCONHECIDO";
        var exitCode = exitMatch.Success && int.TryParse(exitMatch.Groups[1].Value, out var code) ? code : -1;

        return new RunCommandResult(gtvResult, exitCode, output);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
    }
}
