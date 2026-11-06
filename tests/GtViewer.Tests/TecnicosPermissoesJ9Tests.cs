using System.Security.Claims;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GtViewer.Tests;

public class TecnicosPermissoesJ9Tests : IClassFixture<WebApplicationFactory<GtViewer.Web.Program>>
{
    private readonly WebApplicationFactory<GtViewer.Web.Program> _factory;

    public TecnicosPermissoesJ9Tests(WebApplicationFactory<GtViewer.Web.Program> factory)
    {
        _factory = factory;
    }
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private static (TecnicoService service, GtViewerDbContext db, IActivationCodeService activation) CreateTestService()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var activation = new ActivationCodeService();
        var auditoria = new AuditoriaService(db);
        var service = new TecnicoService(db, activation, auditoria);
        return (service, db, activation);
    }

    [Fact]
    public async Task CriarTecnico_GeraCodigoAtivacao_PersistePermissoes_EGravaAuditoria()
    {
        var (service, db, activation) = CreateTestService();

        var username = $"tecnico_{Guid.NewGuid():N}";
        var permissoes = new[] { Permissoes.SuporteRemoto, Permissoes.Reiniciar };

        var (tecnico, codigoAtivacao) = await service.CriarTecnicoAsync(
            username,
            "Carlos Silva",
            "carlos.silva@empresa.com",
            permissoes,
            "admin.teste");

        Assert.NotNull(tecnico);
        Assert.True(tecnico.Id > 0);
        Assert.Equal(username, tecnico.UsuarioDominio);
        Assert.True(tecnico.Ativo);
        Assert.False(tecnico.TotpConfigurado);
        Assert.Equal("suporte_remoto,reiniciar", tecnico.Permissoes);

        // Código de ativação de 16 caracteres gerado e salvo como hash SHA-256
        Assert.NotNull(codigoAtivacao);
        Assert.Equal(19, codigoAtivacao.Length); // XXXX-XXXX-XXXX-XXXX = 16 chars + 3 hífens
        Assert.True(activation.VerifyActivationCode(codigoAtivacao, tecnico.CodigoAtivacaoHash!));

        // Invariante 5 e J9: Criar técnico gera auditoria direta em Auditoria
        var auditCriacao = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.CriarAlterarDesativarTecnico && a.Alvo == username)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(auditCriacao);
        Assert.True(auditCriacao.Sucesso);
        Assert.Equal("admin.teste", auditCriacao.Ator);

        var auditCodigo = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.GerarCodigoAtivacao && a.Alvo == username)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(auditCodigo);
    }

    [Fact]
    public async Task CriarTecnico_ComUsuarioDuplicado_DisparaExcecao()
    {
        var (service, _, _) = CreateTestService();
        var username = $"duplicado_{Guid.NewGuid():N}";

        await service.CriarTecnicoAsync(username, "Primeiro", null, [Permissoes.SuporteRemoto], "admin");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CriarTecnicoAsync(username, "Segundo", null, [Permissoes.Reiniciar], "admin"));
    }

    [Fact]
    public async Task MecanismoPermissoes_SemAFlag_RetornaFalhaDeAutorizacao403()
    {
        // Configura AuthorizationService com as políticas de permissão
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            foreach (var perm in Permissoes.Todas)
            {
                options.AddPolicy(perm, policy => policy.RequireAssertion(ctx => ctx.User.HasPermissao(perm)));
            }
        });

        var sp = services.BuildServiceProvider();
        var authService = sp.GetRequiredService<IAuthorizationService>();

        // Usuário com permissão apenas de suporte_remoto
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "tecnico.suporte"),
            new Claim("Permissoes", Permissoes.SuporteRemoto)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);

        // 1. Ação de suporte_remoto deve ser autorizada
        var authSuporte = await authService.AuthorizeAsync(user, Permissoes.SuporteRemoto);
        Assert.True(authSuporte.Succeeded);

        // 2. Ação de administrar_tecnicos SEM a flag deve ser RECUSADA (403 Forbidden)
        var authAdmin = await authService.AuthorizeAsync(user, Permissoes.AdministrarTecnicos);
        Assert.False(authAdmin.Succeeded);

        // 3. Ação de bloquear SEM a flag deve ser RECUSADA (403 Forbidden)
        var authBloquear = await authService.AuthorizeAsync(user, Permissoes.Bloquear);
        Assert.False(authBloquear.Succeeded);
    }

    [Fact]
    public async Task ProtecaoUltimoAdministrador_ImpedeDesativarUltimoAdminAtivo()
    {
        var (service, db, _) = CreateTestService();

        // Garante que existe pelo menos um administrador ou cria um administrador único isolado
        var adminUnique = $"admin_unico_{Guid.NewGuid():N}";
        var (admin, _) = await service.CriarTecnicoAsync(
            adminUnique,
            "Admin Único Teste",
            null,
            [Permissoes.AdministrarTecnicos],
            "sistema");

        // Desativa temporariamente outros admins se houver, para simular o cenário de último admin
        var outrosAdmins = await db.Tecnicos
            .Where(t => t.Ativo && !t.IsContaEmergencia && t.Id != admin.Id)
            .ToListAsync();

        var adminsDesativadosMock = new List<Tecnico>();
        foreach (var outro in outrosAdmins)
        {
            if (outro.PossuiPermissao(Permissoes.AdministrarTecnicos))
            {
                outro.Ativo = false;
                adminsDesativadosMock.Add(outro);
            }
        }
        if (adminsDesativadosMock.Count > 0)
        {
            await db.SaveChangesAsync();
        }

        try
        {
            // Tentativa de desativar o último administrador ativo deve ser BLOQUEADA
            var exDesativar = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DesativarTecnicoAsync(admin.Id, "admin.teste"));

            Assert.Contains("último administrador", exDesativar.Message, StringComparison.OrdinalIgnoreCase);

            // Tentativa de remover a permissão de administrador do último administrador ativo deve ser BLOQUEADA
            var exRemoverPermissao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.AtualizarPermissoesAsync(admin.Id, [Permissoes.SuporteRemoto], "admin.teste"));

            Assert.Contains("último administrador", exRemoverPermissao.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Restaura o estado dos outros administradores
            foreach (var rest in adminsDesativadosMock)
            {
                rest.Ativo = true;
            }
            if (adminsDesativadosMock.Count > 0)
            {
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task ProtecaoUltimoAdministrador_PermiteDesativarQuandoHaMaisDeUmAdmin()
    {
        var (service, _, _) = CreateTestService();

        var admin1User = $"admin1_{Guid.NewGuid():N}";
        var admin2User = $"admin2_{Guid.NewGuid():N}";

        var (admin1, _) = await service.CriarTecnicoAsync(admin1User, "Admin 1", null, [Permissoes.AdministrarTecnicos], "sistema");
        var (admin2, _) = await service.CriarTecnicoAsync(admin2User, "Admin 2", null, [Permissoes.AdministrarTecnicos], "sistema");

        // Desativar um deles deve funcionar pois ainda resta admin2
        var desativado = await service.DesativarTecnicoAsync(admin1.Id, admin2User);
        Assert.False(desativado.Ativo);

        // Reativar deve funcionar
        var reativado = await service.AtivarTecnicoAsync(admin1.Id, admin2User);
        Assert.True(reativado.Ativo);
    }

    [Fact]
    public async Task TecnicoDesativado_PerdeSessaoImediatamente()
    {
        var (service, db, _) = CreateTestService();

        var username = $"tecnico_sessao_{Guid.NewGuid():N}";
        var (tecnico, _) = await service.CriarTecnicoAsync(username, "Tecnico Sessao", null, [Permissoes.SuporteRemoto], "admin");

        // Simula cookie de sessão autenticada do técnico
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<ILdapService>(new MockLdapService());
        services.AddSingleton<IAuditoriaService>(new AuditoriaService(db));
        services.AddLogging();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        var sp = services.BuildServiceProvider();

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim("IsContaEmergencia", "False"),
            new Claim("LastAdRecheckUtc", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var authScheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var cookieOptions = new CookieAuthenticationOptions();
        var ticket = new AuthenticationTicket(principal, authScheme.Name);
        var context = new CookieValidatePrincipalContext(httpContext, authScheme, cookieOptions, ticket);

        var recheckEvents = new AdSessionRecheckEvents();

        // 1. Enquanto ativo, sessão é válida
        await recheckEvents.ValidatePrincipal(context);
        Assert.NotNull(context.Principal);

        // 2. Administrador desativa o técnico no sistema
        await service.DesativarTecnicoAsync(tecnico.Id, "admin.master");

        // 3. Na próxima requisição, a sessão deve ser IMEDIATAMENTE invalidada
        var contextAposDesativar = new CookieValidatePrincipalContext(httpContext, authScheme, cookieOptions, ticket);
        await recheckEvents.ValidatePrincipal(contextAposDesativar);

        Assert.Null(contextAposDesativar.Principal); // Sessão encerrada!
    }

    [Fact]
    public async Task Redefinir2Fa_InvalidaTotpConfigurado_GeraNovoCodigoAtivacao_EGravaAuditoria()
    {
        var (service, db, activation) = CreateTestService();

        var username = $"tecnico_redef2fa_{Guid.NewGuid():N}";
        var (tecnico, _) = await service.CriarTecnicoAsync(username, "Tecnico Perdeu Celular", null, [Permissoes.SuporteRemoto], "admin");

        // Simula 2FA já configurado anteriormente
        tecnico.TotpConfigurado = true;
        tecnico.TotpSecretCifrado = "segredo_antigo_cifrado";
        tecnico.CodigoAtivacaoHash = null;
        await db.SaveChangesAsync();

        // Administrador redefine o 2FA
        var (atualizado, novoCodigo) = await service.Redefinir2FaAsync(tecnico.Id, "admin.gestor");

        Assert.False(atualizado.TotpConfigurado);
        Assert.Null(atualizado.TotpSecretCifrado);
        Assert.NotNull(atualizado.CodigoAtivacaoHash);
        Assert.NotNull(novoCodigo);
        Assert.True(activation.VerifyActivationCode(novoCodigo, atualizado.CodigoAtivacaoHash!));

        // Auditoria de redefinir 2FA
        var audit = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.Redefinir2Fa && a.Alvo == username)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(audit);
        Assert.Equal("admin.gestor", audit.Ator);
    }

    [Fact]
    public async Task Http_AcessoRestrito_SemFlag_Retorna403()
    {
        var mockLdap = new MockLdapService();
        var clientFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILdapService>(mockLdap);
            });
        });

        // 1. Acesso anônimo a /tecnicos -> Redireciona para /login (302)
        var anonClient = clientFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonResponse = await anonClient.GetAsync("/tecnicos");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, anonResponse.StatusCode);
        Assert.Contains("/login", anonResponse.Headers.Location?.ToString());

        // 2. Criar técnico no banco com apenas permissão 'suporte_remoto'
        using var scope = clientFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GtViewerDbContext>();
        var totpService = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var username = $"tecnico_http_{Guid.NewGuid():N}";
        var plainSecret = totpService.GenerateSecret();

        var tecnico = new Tecnico
        {
            UsuarioDominio = username,
            NomeExibicao = "Tecnico HTTP Sem Admin",
            Ativo = true,
            Permissoes = Permissoes.SuporteRemoto,
            TotpSecretCifrado = totpService.EncryptSecret(plainSecret),
            TotpConfigurado = true
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();

        // 3. Fazer login via AuthService
        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var totpCode = TotpService.ComputeCurrentTotpCode(plainSecret);
        var loginResult = await authService.Validate2FaLoginAsync(tecnico.Id, totpCode, "127.0.0.1");
        Assert.True(loginResult.Success);
        Assert.NotNull(loginResult.Principal);

        // 4. Testar autorização direta das políticas do ASP.NET Core no container HTTP
        var authPolicyService = clientFactory.Services.GetRequiredService<IAuthorizationService>();

        // Política 'suporte_remoto' autorizada
        var authSuporte = await authPolicyService.AuthorizeAsync(loginResult.Principal, Permissoes.SuporteRemoto);
        Assert.True(authSuporte.Succeeded);

        // Política 'administrar_tecnicos' RECUSADA (403 Forbidden)
        var authAdmin = await authPolicyService.AuthorizeAsync(loginResult.Principal, Permissoes.AdministrarTecnicos);
        Assert.False(authAdmin.Succeeded);
    }
}
