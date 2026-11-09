using System.Text.Json.Nodes;
using GtViewer.Web.Models.MeshCentral;
using GtViewer.Web.Services;

namespace GtViewer.Tests.Mocks;

public class MockMeshCentralClient : IMeshCentralClient
{
    public bool IsConnected { get; set; } = true;
    public Dictionary<string, JsonNode> NodesStorage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, JsonNode> CachedNodes => NodesStorage;

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<MeshNodeEventArgs>? NodeConnected;
    public event EventHandler<MeshNodeEventArgs>? NodeDisconnected;
    public event EventHandler<MeshNodeEventArgs>? NodeChanged;
    public event EventHandler<FullSyncEventArgs>? FullSyncCompleted;
    public event EventHandler<string>? RawMessageReceived;

    public int SendRequestCalls { get; set; }
    public int RunScriptCalls { get; set; }
    public int RestartCalls { get; set; }
    public int EnsureMirrorUserCalls { get; set; }
    public int GrantRightsCalls { get; set; }
    public int RevokeRightsCalls { get; set; }
    public int DropSessionsCalls { get; set; }

    public Func<object, string?, JsonNode?>? SendRequestHandler { get; set; }
    public Func<string, string, RunCommandResult>? RunScriptHandler { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        ConnectionStateChanged?.Invoke(this, true);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        ConnectionStateChanged?.Invoke(this, false);
        return Task.CompletedTask;
    }

    public Task<JsonNode?> SendRequestAsync(object payload, string? responseId = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MeshCentralDisconnectedException("Mock MeshCentral desconectado.");
        }

        SendRequestCalls++;
        if (SendRequestHandler != null)
        {
            return Task.FromResult(SendRequestHandler(payload, responseId));
        }

        return Task.FromResult<JsonNode?>(new JsonObject { ["responseid"] = responseId ?? "resp_mock", ["result"] = "ok" });
    }

    public Task<JsonNode?> GetServerInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendRequestAsync(new { action = "serverinfo" }, timeout: timeout, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, JsonNode>> GetNodesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MeshCentralDisconnectedException("Mock MeshCentral desconectado.");
        }

        return Task.FromResult<IReadOnlyDictionary<string, JsonNode>>(NodesStorage);
    }

    public Task<JsonNode?> GetSysInfoAsync(string nodeId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return SendRequestAsync(new { action = "getsysinfo", nodeid = nodeId }, timeout: timeout, cancellationToken: cancellationToken);
    }

    public Task<RunCommandResult> RunFixedScriptAsync(string nodeId, string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MeshCentralDisconnectedException("Mock MeshCentral desconectado.");
        }

        RunScriptCalls++;
        if (RunScriptHandler != null)
        {
            return Task.FromResult(RunScriptHandler(nodeId, script));
        }

        return Task.FromResult(new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0"));
    }

    public Task<string> EnsureMirrorUserAsync(string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        EnsureMirrorUserCalls++;
        var mirrorUser = technicianUsername.StartsWith("gtv_mirror_") ? technicianUsername : $"gtv_mirror_{technicianUsername}";
        return Task.FromResult(mirrorUser);
    }

    public Task<bool> GrantRemoteDesktopRightsAsync(string nodeId, string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        GrantRightsCalls++;
        return Task.FromResult(true);
    }

    public Task<bool> RevokeRemoteDesktopRightsAsync(string nodeId, string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        RevokeRightsCalls++;
        return Task.FromResult(true);
    }

    public Task<bool> DropUserSessionsAsync(string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        DropSessionsCalls++;
        return Task.FromResult(true);
    }

    public Task<bool> SendToastAsync(string nodeId, string title, string message, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<RunCommandResult> RequestRestartAsync(string nodeId, TimeSpan? delay = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MeshCentralDisconnectedException("Mock MeshCentral desconectado. Ação de reinício recusada.");
        }

        RestartCalls++;
        return Task.FromResult(new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0"));
    }

    public void TriggerNodeConnect(string nodeId, JsonNode? raw = null)
    {
        NodeConnected?.Invoke(this, new MeshNodeEventArgs(nodeId, raw));
    }

    public void TriggerNodeDisconnect(string nodeId, JsonNode? raw = null)
    {
        NodeDisconnected?.Invoke(this, new MeshNodeEventArgs(nodeId, raw));
    }

    public void TriggerNodeChange(string nodeId, JsonNode? raw = null)
    {
        NodeChanged?.Invoke(this, new MeshNodeEventArgs(nodeId, raw));
    }

    public void TriggerConnectionStateChanged(bool connected)
    {
        IsConnected = connected;
        ConnectionStateChanged?.Invoke(this, connected);
    }

    public void TriggerFullSync(IReadOnlyDictionary<string, JsonNode> nodes)
    {
        FullSyncCompleted?.Invoke(this, new FullSyncEventArgs(nodes));
    }

    public void TriggerRawMessage(string message)
    {
        RawMessageReceived?.Invoke(this, message);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
