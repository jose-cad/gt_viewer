using System.Net;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GtViewer.Tests;

public class InventarioUiTests : IClassFixture<WebApplicationFactory<GtViewer.Web.Program>>
{
    private readonly WebApplicationFactory<GtViewer.Web.Program> _factory;

    public InventarioUiTests(WebApplicationFactory<GtViewer.Web.Program> factory)
    {
        _factory = factory;
    }

    private (HttpClient Client, IDisposable Scope) CreateClientWithAuth(params string[] permissoes)
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
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
    public async Task MaquinasIndex_AuthenticatedUser_RendersSuccessfully()
    {
        var (client, scope) = CreateClientWithAuth(); // Sem permissão específica, mas autenticado
        using var _ = scope;
        using var __ = client;

        client.DefaultRequestHeaders.Add("X-Test-User", "tecnico_comum");

        var response = await client.GetAsync("/Maquinas/Index");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Inventário de Máquinas", html);
    }

    [Fact]
    public async Task MaquinasAprovacao_WithoutPermission_ReturnsForbidden()
    {
        var (client, scope) = CreateClientWithAuth(Permissoes.SuporteRemoto); // Tem apenas suporte remoto
        using var _ = scope;
        using var __ = client;

        client.DefaultRequestHeaders.Add("X-Test-User", "tecnico_sem_cadastrar");
        client.DefaultRequestHeaders.Add("X-Test-Permissions", Permissoes.SuporteRemoto);

        var response = await client.GetAsync("/Maquinas/Aprovacao");

        // Acesso negado pela política CadastrarMaquinas (redireciona para /error?code=403 ou retorna 403)
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden ||
                    (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.ToString().Contains("403") == true));
    }

    [Fact]
    public async Task MaquinasAprovacao_WithPermission_RendersSuccessfully()
    {
        var (client, scope) = CreateClientWithAuth(Permissoes.CadastrarMaquinas);
        using var _ = scope;
        using var __ = client;

        client.DefaultRequestHeaders.Add("X-Test-User", "tecnico_cadastrador");
        client.DefaultRequestHeaders.Add("X-Test-Permissions", Permissoes.CadastrarMaquinas);

        var response = await client.GetAsync("/Maquinas/Aprovacao");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Aprovação de Conflitos", html);
    }

    [Fact]
    public async Task DownloadInstalador_Post_DeliversFileAndAudits()
    {
        var (client, scope) = CreateClientWithAuth(Permissoes.CadastrarMaquinas);
        using var _ = scope;
        using var __ = client;

        var username = "tecnico_download_" + Guid.NewGuid().ToString("N")[..6];
        client.DefaultRequestHeaders.Add("X-Test-User", username);
        client.DefaultRequestHeaders.Add("X-Test-Permissions", Permissoes.CadastrarMaquinas);

        var getResponse = await client.GetAsync("/Maquinas/DownloadInstalador");
        var getHtml = await getResponse.Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(getHtml, @"name=""__RequestVerificationToken""\s+(?:type=""hidden""\s+)?value=""([^""]+)""");
        var token = match.Success ? match.Groups[1].Value : string.Empty;

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "__RequestVerificationToken", token }
        });

        var postResponse = await client.PostAsync("/Maquinas/DownloadInstalador?handler=Download", formContent);

        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);
        Assert.Equal("application/octet-stream", postResponse.Content.Headers.ContentType?.MediaType);

        // Confere auditoria registrada no banco
        using var dbScope = _factory.Services.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<GtViewerDbContext>();

        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.Ator == username && a.Tipo == TiposAuditoria.BaixarInstalador);
        Assert.NotNull(audit);
        Assert.True(audit.Sucesso);
    }
}
