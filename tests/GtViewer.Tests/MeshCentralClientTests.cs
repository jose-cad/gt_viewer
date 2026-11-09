using System.Text.Json.Nodes;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Models;
using GtViewer.Web.Models.MeshCentral;
using GtViewer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GtViewer.Tests;

public class MeshCentralClientTests
{
    private static (MeshCentralClient client, TestMeshWebSocketConnection connection) CreateClient(
        int reconnectIntervalSeconds = 1,
        bool autoConnect = false)
    {
        var connection = new TestMeshWebSocketConnection();
        var options = Options.Create(new MeshCentralOptions
        {
            WebSocketUrl = "wss://mesh.localhost/control.ashx",
            ServiceUsername = "admin",
            ServicePassword = "admin_dev_password_123",
            AutoConnect = autoConnect,
            ReconnectIntervalSeconds = reconnectIntervalSeconds,
            IgnoreSslErrors = true
        });

        var client = new MeshCentralClient(
            options,
            NullLogger<MeshCentralClient>.Instance,
            () => connection);

        return (client, connection);
    }

    [Fact]
    public async Task ConnectAsync_EstablishesConnectionAndSetsIsConnectedTrue()
    {
        var (client, connection) = CreateClient();
        var stateChangedFired = false;
        client.ConnectionStateChanged += (_, connected) => stateChangedFired = connected;

        await client.ConnectAsync();
        await Task.Delay(50);

        Assert.True(client.IsConnected);
        Assert.True(stateChangedFired);
        Assert.Equal("admin", connection.ConnectedUsername);
        Assert.Equal("admin_dev_password_123", connection.ConnectedPassword);

        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task SendRequestAsync_CorrelatesResponseByResponseId()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        connection.OnMessageSentAsync = msg =>
        {
            var node = JsonNode.Parse(msg);
            var responseId = node?["responseid"]?.ToString();
            var reply = new JsonObject
            {
                ["action"] = "serverinfo",
                ["responseid"] = responseId,
                ["serverinfo"] = new JsonObject
                {
                    ["name"] = "mesh.test.local",
                    ["features"] = 1234
                }
            };
            connection.EnqueueIncomingMessage(reply.ToJsonString());
            return Task.CompletedTask;
        };

        var result = await client.GetServerInfoAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(result);
        Assert.Equal("mesh.test.local", result["name"]?.ToString());
        Assert.Equal(1234, result["features"]?.GetValue<int>());

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SendRequestAsync_MultipleConcurrentCalls_ReceiveRespectiveResponsesCorrectly()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        const int callCount = 20;

        // Responde de forma assíncrona simulando respostas com atrasos variados e fora de ordem
        connection.OnMessageSentAsync = async msg =>
        {
            var node = JsonNode.Parse(msg);
            var responseId = node?["responseid"]?.ToString();
            var tag = node?["custom_tag"]?.ToString();

            // Adiciona pequeno atraso aleatório para provocar respostas fora de ordem
            await Task.Delay(Random.Shared.Next(5, 25));

            var reply = new JsonObject
            {
                ["responseid"] = responseId,
                ["echo_tag"] = tag,
                ["result"] = $"ok_{tag}"
            };
            connection.EnqueueIncomingMessage(reply.ToJsonString());
        };

        var tasks = Enumerable.Range(1, callCount).Select(async i =>
        {
            var payload = new { action = "test", custom_tag = $"req_{i}" };
            var response = await client.SendRequestAsync(payload, timeout: TimeSpan.FromSeconds(5));
            return (ExpectedTag: $"req_{i}", ActualEcho: response?["echo_tag"]?.ToString(), Result: response?["result"]?.ToString());
        });

        var results = await Task.WhenAll(tasks);

        Assert.Equal(callCount, results.Length);
        foreach (var r in results)
        {
            Assert.Equal(r.ExpectedTag, r.ActualEcho);
            Assert.Equal($"ok_{r.ExpectedTag}", r.Result);
        }

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task SendRequestAsync_WhenDisconnected_ThrowsMeshCentralDisconnectedException()
    {
        var (client, _) = CreateClient();
        // Não chamou ConnectAsync()

        await Assert.ThrowsAsync<MeshCentralDisconnectedException>(async () =>
        {
            await client.SendRequestAsync(new { action = "nodes" });
        });
    }

    [Fact]
    public async Task SendRequestAsync_WhenTimeoutOccurs_ThrowsMeshCentralTimeoutException()
    {
        var (client, _) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        // Nenhuma resposta é enfileirada no WebSocket simulado

        await Assert.ThrowsAsync<MeshCentralTimeoutException>(async () =>
        {
            await client.SendRequestAsync(new { action = "nodes" }, timeout: TimeSpan.FromMilliseconds(100));
        });

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task DispatchEvents_NodeConnect_FiresNodeConnectedEvent()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        string? connectedNodeId = null;
        var tcs = new TaskCompletionSource<bool>();

        client.NodeConnected += (_, args) =>
        {
            connectedNodeId = args.NodeId;
            tcs.TrySetResult(true);
        };

        // Simula evento do MeshCentral
        var evt = new JsonObject
        {
            ["action"] = "event",
            ["event"] = new JsonObject
            {
                ["action"] = "nodeconnect",
                ["nodeid"] = "node//test_pc_01"
            }
        };
        connection.EnqueueIncomingMessage(evt.ToJsonString());

        await Task.WhenAny(tcs.Task, Task.Delay(3000));

        Assert.Equal("node//test_pc_01", connectedNodeId);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task DispatchEvents_NodeDisconnect_FiresNodeDisconnectedEvent()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        string? disconnectedNodeId = null;
        var tcs = new TaskCompletionSource<bool>();

        client.NodeDisconnected += (_, args) =>
        {
            disconnectedNodeId = args.NodeId;
            tcs.TrySetResult(true);
        };

        var evt = new JsonObject
        {
            ["action"] = "event",
            ["event"] = new JsonObject
            {
                ["action"] = "nodedisconnect",
                ["nodeid"] = "node//test_pc_02"
            }
        };
        connection.EnqueueIncomingMessage(evt.ToJsonString());

        await Task.WhenAny(tcs.Task, Task.Delay(3000));

        Assert.Equal("node//test_pc_02", disconnectedNodeId);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task DispatchEvents_NodeChange_FiresNodeChangedEvent()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        string? changedNodeId = null;
        var tcs = new TaskCompletionSource<bool>();

        client.NodeChanged += (_, args) =>
        {
            changedNodeId = args.NodeId;
            tcs.TrySetResult(true);
        };

        var evt = new JsonObject
        {
            ["action"] = "event",
            ["event"] = new JsonObject
            {
                ["action"] = "changenode",
                ["nodeid"] = "node//test_pc_03"
            }
        };
        connection.EnqueueIncomingMessage(evt.ToJsonString());

        await Task.WhenAny(tcs.Task, Task.Delay(3000));

        Assert.Equal("node//test_pc_03", changedNodeId);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task Reconnection_WhenConnectionDrops_ReconnectsAndFiresFullSyncCompleted()
    {
        var connection1 = new TestMeshWebSocketConnection();
        var connection2 = new TestMeshWebSocketConnection();
        var connectionIndex = 0;

        var options = Options.Create(new MeshCentralOptions
        {
            WebSocketUrl = "wss://mesh.localhost/control.ashx",
            ServiceUsername = "admin",
            ServicePassword = "admin_dev_password_123",
            AutoConnect = false,
            ReconnectIntervalSeconds = 1,
            IgnoreSslErrors = true
        });

        // Configura respostas para ambos os sockets quando solicitarem "nodes"
        void SetupNodesReply(TestMeshWebSocketConnection conn, string nodeId)
        {
            conn.OnMessageSentAsync = msg =>
            {
                var node = JsonNode.Parse(msg);
                if (node?["action"]?.ToString() == "nodes")
                {
                    var responseId = node["responseid"]?.ToString();
                    var reply = new JsonObject
                    {
                        ["action"] = "nodes",
                        ["responseid"] = responseId,
                        ["nodes"] = new JsonObject
                        {
                            ["mesh1"] = new JsonObject
                            {
                                [nodeId] = new JsonObject
                                {
                                    ["_id"] = nodeId,
                                    ["name"] = "PC-Sync"
                                }
                            }
                        }
                    };
                    conn.EnqueueIncomingMessage(reply.ToJsonString());
                }
                return Task.CompletedTask;
            };
        }

        SetupNodesReply(connection1, "node//conn1");
        SetupNodesReply(connection2, "node//conn2");

        var client = new MeshCentralClient(
            options,
            NullLogger<MeshCentralClient>.Instance,
            () =>
            {
                var idx = Interlocked.Increment(ref connectionIndex);
                return idx == 1 ? connection1 : connection2;
            });

        var syncCount = 0;
        var syncTcs = new TaskCompletionSource<bool>();

        client.FullSyncCompleted += (_, args) =>
        {
            syncCount++;
            if (syncCount == 2)
            {
                syncTcs.TrySetResult(true);
            }
        };

        // 1. Primeira conexão
        await client.ConnectAsync();
        await Task.Delay(100);
        Assert.True(client.IsConnected);

        // 2. Derruba o primeiro socket
        connection1.CloseServerSide();
        await Task.Delay(100);
        Assert.False(client.IsConnected);

        // 3. Aguarda reconexão automática (intervalo de 1s)
        await Task.WhenAny(syncTcs.Task, Task.Delay(3000));

        Assert.True(client.IsConnected);
        Assert.True(syncCount >= 2);
        Assert.True(client.CachedNodes.ContainsKey("node//conn2"));

        await client.DisconnectAsync();
    }

    [Theory]
    [InlineData("GTV-RESULT=OK\r\nGTV-EXIT=0", "OK", 0)]
    [InlineData("Log previo...\nGTV-RESULT=JA_INSTALADO\nGTV-EXIT=0\nFinal.", "JA_INSTALADO", 0)]
    [InlineData("Erro na instalacao\nGTV-RESULT=FALHA\nGTV-EXIT=1603", "FALHA", 1603)]
    [InlineData("GTV-RESULT=BLOQUEADA\r\nGTV-EXIT=0", "BLOQUEADA", 0)]
    [InlineData("GTV-RESULT=OCUPADO\r\nGTV-EXIT=0", "OCUPADO", 0)]
    [InlineData("Sem marcadores aqui", "DESCONHECIDO", -1)]
    public void ParseRunCommandResult_ExtractsResultAndExitCodeProperly(string output, string expectedResult, int expectedExit)
    {
        var parsed = MeshCentralClient.ParseRunCommandResult(output);

        Assert.Equal(expectedResult, parsed.GtvResult);
        Assert.Equal(expectedExit, parsed.ExitCode);
        Assert.Equal(output, parsed.RawOutput);
    }

    [Fact]
    public async Task RunFixedScriptAsync_ExecutesScriptAndParsesOutput()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        connection.OnMessageSentAsync = msg =>
        {
            var node = JsonNode.Parse(msg);
            var responseId = node?["responseid"]?.ToString();
            var reply = new JsonObject
            {
                ["action"] = "msg",
                ["type"] = "runcommands",
                ["responseid"] = responseId,
                ["result"] = "Saida do script...\r\nGTV-RESULT=OK\r\nGTV-EXIT=0"
            };
            connection.EnqueueIncomingMessage(reply.ToJsonString());
            return Task.CompletedTask;
        };

        var result = await client.RunFixedScriptAsync("node//win11_dev", "Write-Output 'Teste'", TimeSpan.FromSeconds(2));

        Assert.Equal("OK", result.GtvResult);
        Assert.Equal(0, result.ExitCode);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task RequestRestartAsync_WhenOffline_ThrowsMeshCentralDisconnectedException()
    {
        var (client, _) = CreateClient();
        // Não conectado

        await Assert.ThrowsAsync<MeshCentralDisconnectedException>(async () =>
        {
            await client.RequestRestartAsync("node//win11_pc");
        });
    }

    [Fact]
    public async Task RequestRestartAsync_WhenConnected_ExecutesScriptWithShutdownNotice()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        string? executedScript = null;

        connection.OnMessageSentAsync = msg =>
        {
            var node = JsonNode.Parse(msg);
            executedScript = node?["cmds"]?.ToString();
            var responseId = node?["responseid"]?.ToString();
            var reply = new JsonObject
            {
                ["action"] = "msg",
                ["type"] = "runcommands",
                ["responseid"] = responseId,
                ["result"] = "GTV-RESULT=OK\r\nGTV-EXIT=0"
            };
            connection.EnqueueIncomingMessage(reply.ToJsonString());
            return Task.CompletedTask;
        };

        var result = await client.RequestRestartAsync("node//win11_pc", delay: TimeSpan.FromMinutes(2));

        Assert.Equal("OK", result.GtvResult);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(executedScript);
        Assert.Contains("shutdown.exe /r /t 120", executedScript);
        Assert.Contains("GTV-RESULT=OK", executedScript);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task EnsureMirrorUserAsync_WhenUserDoesNotExist_CreatesUserWithoutAdminRights()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        var addedUserJson = "";

        connection.OnMessageSentAsync = msg =>
        {
            var node = JsonNode.Parse(msg);
            var action = node?["action"]?.ToString();
            var responseId = node?["responseid"]?.ToString();

            if (action == "users")
            {
                // Lista de usuários sem o novo técnico
                var reply = new JsonObject
                {
                    ["action"] = "users",
                    ["responseid"] = responseId,
                    ["users"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "admin" }
                    }
                };
                connection.EnqueueIncomingMessage(reply.ToJsonString());
            }
            else if (action == "adduser")
            {
                addedUserJson = msg;
                var reply = new JsonObject
                {
                    ["action"] = "adduser",
                    ["responseid"] = responseId,
                    ["result"] = "ok"
                };
                connection.EnqueueIncomingMessage(reply.ToJsonString());
            }
            return Task.CompletedTask;
        };

        var mirrorUser = await client.EnsureMirrorUserAsync("rodrigo");

        Assert.Equal("gtv_mirror_rodrigo", mirrorUser);
        Assert.NotEmpty(addedUserJson);

        var addedNode = JsonNode.Parse(addedUserJson);
        Assert.Equal("gtv_mirror_rodrigo", addedNode?["username"]?.ToString());
        Assert.Equal(0, addedNode?["siteadmin"]?.GetValue<int>()); // Sem privilégio de siteadmin!

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task GrantAndRevokeRemoteDesktopRights_UsesMinimalBitmask0x08AndRemovesCorrectly()
    {
        var (client, connection) = CreateClient();
        await client.ConnectAsync();
        await Task.Delay(50);

        string? grantMessage = null;
        string? revokeMessage = null;

        connection.OnMessageSentAsync = msg =>
        {
            var node = JsonNode.Parse(msg);
            var responseId = node?["responseid"]?.ToString();
            var remove = node?["remove"]?.GetValue<bool>() ?? false;

            if (remove)
            {
                revokeMessage = msg;
            }
            else
            {
                grantMessage = msg;
            }

            var reply = new JsonObject
            {
                ["responseid"] = responseId,
                ["result"] = "ok"
            };
            connection.EnqueueIncomingMessage(reply.ToJsonString());
            return Task.CompletedTask;
        };

        var grantOk = await client.GrantRemoteDesktopRightsAsync("node//pc01", "carlos");
        var revokeOk = await client.RevokeRemoteDesktopRightsAsync("node//pc01", "carlos");

        Assert.True(grantOk);
        Assert.True(revokeOk);

        Assert.NotNull(grantMessage);
        var grantNode = JsonNode.Parse(grantMessage);
        Assert.Equal(8, grantNode?["rights"]?.GetValue<int>()); // MESHRIGHT_REMOTECONTROL (0x08) estrito
        Assert.Equal("gtv_mirror_carlos", grantNode?["usernames"]?[0]?.ToString());

        Assert.NotNull(revokeMessage);
        var revokeNode = JsonNode.Parse(revokeMessage);
        Assert.True(revokeNode?["remove"]?.GetValue<bool>());

        await client.DisconnectAsync();
    }
}
