using GtViewer.Web.Models;
using GtViewer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GtViewer.Tests;

public class MeshCentralContractLiveTests
{
    private static MeshCentralClient CreateLiveClient()
    {
        var options = Options.Create(new MeshCentralOptions
        {
            WebSocketUrl = "wss://mesh.localhost/control.ashx",
            ServiceUsername = "admin",
            ServicePassword = "admin_dev_password_123",
            AutoConnect = false,
            ReconnectIntervalSeconds = 2,
            IgnoreSslErrors = true
        });

        return new MeshCentralClient(options, NullLogger<MeshCentralClient>.Instance);
    }

    [Fact]
    public async Task LiveMeshCentral_ServerInfo_ReturnsValidMeshCentral131Metadata()
    {
        await using var client = CreateLiveClient();
        await client.ConnectAsync();

        Assert.True(client.IsConnected, "O cliente deve conseguir se conectar ao MeshCentral v1.3.1 em execução no Docker.");

        var serverInfo = await client.GetServerInfoAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(serverInfo);
        Assert.NotNull(serverInfo["name"]);
        Assert.NotNull(serverInfo["features"]);
        Assert.Equal(120, serverInfo["consent"]?.GetValue<int>()); // Configurado na PoC

        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task LiveMeshCentral_GetNodes_ReturnsNodesDictionarySuccessfully()
    {
        await using var client = CreateLiveClient();
        await client.ConnectAsync();

        var nodes = await client.GetNodesAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(nodes);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task LiveMeshCentral_EnsureMirrorUser_CreatesUserSuccessfully()
    {
        await using var client = CreateLiveClient();
        await client.ConnectAsync();

        var uniqueTechName = "tech_" + Guid.NewGuid().ToString("N")[..6];
        var mirrorUser = await client.EnsureMirrorUserAsync(uniqueTechName, TimeSpan.FromSeconds(5));

        Assert.Equal($"gtv_mirror_{uniqueTechName}", mirrorUser);

        // Chamar novamente deve ser idempotente
        var sameMirrorUser = await client.EnsureMirrorUserAsync(uniqueTechName, TimeSpan.FromSeconds(5));
        Assert.Equal(mirrorUser, sameMirrorUser);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task LiveMeshCentral_ConcurrentRequests_AllCompleteSuccessfully()
    {
        await using var client = CreateLiveClient();
        await client.ConnectAsync();

        const int concurrentCount = 10;
        var tasks = Enumerable.Range(1, concurrentCount).Select(async _ =>
        {
            var nodes = await client.GetNodesAsync(TimeSpan.FromSeconds(10));
            return nodes != null;
        });

        var results = await Task.WhenAll(tasks);

        Assert.Equal(concurrentCount, results.Length);
        Assert.All(results, Assert.True);

        await client.DisconnectAsync();
    }
}
