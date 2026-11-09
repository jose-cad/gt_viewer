using System.Net;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Models.MeshCentral;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GtViewer.Tests;

public class MeshCentralUiWarningTests : IClassFixture<WebApplicationFactory<GtViewer.Web.Program>>
{
    private readonly WebApplicationFactory<GtViewer.Web.Program> _factory;

    public MeshCentralUiWarningTests(WebApplicationFactory<GtViewer.Web.Program> factory)
    {
        _factory = factory;
    }

    private (HttpClient Client, IDisposable Scope) CreateClientWithAuth(MockMeshCentralClient mockMeshClient, params string[] permissoes)
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Substitui IMeshCentralClient pelo mock controlado
                services.AddSingleton<IMeshCentralClient>(mockMeshClient);

                // Configura autenticação simulada com as permissões fornecidas
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, options => { });
            });
        });

        var client = customFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        return (client, customFactory);
    }

    [Fact]
    public async Task WhenMeshCentralIsOffline_LayoutDisplaysWarningBanner()
    {
        var mockMesh = new MockMeshCentralClient { IsConnected = false };
        var (client, scope) = CreateClientWithAuth(mockMesh, Permissoes.SuporteRemoto);
        using var _ = scope;
        using var __ = client;

        client.DefaultRequestHeaders.Add("X-Test-User", "tecnico_ana");

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("meshcentral-offline-banner", html);
        Assert.Contains("Servidor de Agentes Desconectado", html);
    }

    [Fact]
    public async Task WhenMeshCentralIsOffline_RemoteSupportRefusesScreenAccessAndDisplaysAlert()
    {
        var mockMesh = new MockMeshCentralClient { IsConnected = false };
        var (client, scope) = CreateClientWithAuth(mockMesh, Permissoes.SuporteRemoto);
        using var _ = scope;
        using var __ = client;

        client.DefaultRequestHeaders.Add("X-Test-User", "tecnico_ana");

        var response = await client.GetAsync("/RemoteSupport?nodeId=node//win11_dev");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("meshcentral-refusal-alert", html);
        Assert.Contains("Acesso à Tela Remota Recusado", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("meshRemoteIframe", html);
    }

    [Fact]
    public async Task WhenMeshCentralIsOnline_RemoteSupportRendersIframeProperly()
    {
        var mockMesh = new MockMeshCentralClient { IsConnected = true };
        var (client, scope) = CreateClientWithAuth(mockMesh, Permissoes.SuporteRemoto);
        using var _ = scope;
        using var __ = client;

        client.DefaultRequestHeaders.Add("X-Test-User", "tecnico_ana");

        var response = await client.GetAsync("/RemoteSupport?nodeId=node//win11_dev");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("meshcentral-refusal-alert", html);
        Assert.Contains("meshRemoteIframe", html);
        Assert.Contains("viewmode=11", html);
    }

    [Fact]
    public async Task WhenMeshCentralIsOffline_RestartActionIsRefused()
    {
        var mockMesh = new MockMeshCentralClient { IsConnected = false };

        var ex = await Assert.ThrowsAsync<MeshCentralDisconnectedException>(async () =>
        {
            await mockMesh.RequestRestartAsync("node//win11_offline");
        });

        Assert.Contains("recusada", ex.Message);
    }
}
