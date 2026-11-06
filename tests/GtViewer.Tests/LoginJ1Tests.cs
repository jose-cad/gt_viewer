using System.Diagnostics;
using System.Security.Claims;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GtViewer.Tests;

public class LoginJ1Tests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private static (AuthService authService, MockLdapService mockLdap, ILoginSecurityService security, ITotpService totp, IActivationCodeService activation, GtViewerDbContext db) CreateTestServices()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var mockLdap = new MockLdapService();
        var dataProtection = new EphemeralDataProtectionProvider();
        var totp = new TotpService(dataProtection);
        var activation = new ActivationCodeService();
        var security = new LoginSecurityService();
        var auditoria = new AuditoriaService(db);

        var authService = new AuthService(db, mockLdap, totp, activation, security, auditoria);
        return (authService, mockLdap, security, totp, activation, db);
    }

    [Fact]
    public async Task UnregisteredUser_DoesNotReachLdap_AndAuditsFailure()
    {
        var (authService, mockLdap, _, _, _, db) = CreateTestServices();
        var randomUser = $"inexistente_{Guid.NewGuid():N}";

        var result = await authService.AuthenticateInitialAsync(randomUser, "QualquerSenha123!", "10.0.0.99");

        Assert.False(result.Success);
        Assert.Equal("Credenciais inválidas.", result.Error);
        // Invariante e Tarefa 6: Usuário não cadastrado NUNCA consulta o AD
        Assert.Equal(0, mockLdap.AuthenticateCalls);

        // Verifica auditoria
        var audit = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.LoginFalho && a.Ator == randomUser)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(audit);
        Assert.False(audit.Sucesso);
    }

    [Fact]
    public async Task TimingAttackProtection_EnsuresMinimumConstantResponseTime()
    {
        var (authService, _, _, _, _, _) = CreateTestServices();
        var sw = Stopwatch.StartNew();

        await authService.AuthenticateInitialAsync("usuario.fantasma", "senha123", "10.0.0.10");
        sw.Stop();

        // O tempo mínimo de resposta configurado é de 500ms
        Assert.True(sw.ElapsedMilliseconds >= 450, $"Tempo foi {sw.ElapsedMilliseconds}ms, esperado >= 450ms.");
    }

    [Fact]
    public async Task IpRateLimiting_MoreThanTenFailuresBlocksIp_AndIsolatesOtherIps()
    {
        var (authService, _, security, _, _, _) = CreateTestServices();
        var attackerIp = $"192.168.10.{Random.Shared.Next(10, 200)}";
        var legitimateIp = $"192.168.10.{Random.Shared.Next(201, 250)}";

        // 11 tentativas falhas a partir do IP do atacante
        for (int i = 0; i < 11; i++)
        {
            await authService.AuthenticateInitialAsync("user.tentativa", "senha_errada", attackerIp);
        }

        // Verifica que o IP do atacante foi bloqueado
        Assert.True(security.IsIpBlocked(attackerIp));
        // Invariante e Tarefa 6: Outro IP NÃO é bloqueado (isolamento por IP real)
        Assert.False(security.IsIpBlocked(legitimateIp));
    }

    [Fact]
    public async Task ActivationCode_OnlyCountsAttemptsWithCorrectAdPassword_AndInvalidatesAfterFiveErrors()
    {
        var (authService, _, _, _, activationService, db) = CreateTestServices();

        var username = $"tecnico_ativacao_{Guid.NewGuid():N}";
        var activationCode = activationService.GenerateActivationCode();

        var tecnico = new Tecnico
        {
            UsuarioDominio = username,
            NomeExibicao = "Técnico Teste Ativação",
            Ativo = true,
            Permissoes = Permissoes.SuporteRemoto,
            CodigoAtivacaoHash = activationService.HashActivationCode(activationCode),
            CodigoAtivacaoCriadoEm = DateTimeOffset.UtcNow,
            CodigoAtivacaoTentativas = 0,
            TotpConfigurado = false
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();

        // 1. Senha do AD errada -> Não deve nem chegar na ativação
        var resultWrongPw = await authService.AuthenticateInitialAsync(username, "SenhaErrada!", "127.0.0.1");
        Assert.False(resultWrongPw.Success);
        var t1 = await db.Tecnicos.FindAsync(tecnico.Id);
        Assert.Equal(0, t1!.CodigoAtivacaoTentativas);

        // 2. Senha do AD correta -> Requer ativação
        var resultRightPw = await authService.AuthenticateInitialAsync(username, "SenhaCorreta123!", "127.0.0.1");
        Assert.True(resultRightPw.Success);
        Assert.True(resultRightPw.RequiresActivation);

        // 3. Tentar código de ativação errado 4 vezes
        for (int i = 1; i <= 4; i++)
        {
            var actFail = await authService.ValidateActivationCodeAndBegin2FaSetupAsync(tecnico.Id, "CODIGO-ERRADO-XXXX", "127.0.0.1");
            Assert.False(actFail.Success);
            var tCheck = await db.Tecnicos.FindAsync(tecnico.Id);
            Assert.Equal(i, tCheck!.CodigoAtivacaoTentativas);
            Assert.NotNull(tCheck.CodigoAtivacaoHash); // Ainda não invalidou
        }

        // 4. 5ª tentativa errada -> Invalida permanentemente o código
        var actFail5 = await authService.ValidateActivationCodeAndBegin2FaSetupAsync(tecnico.Id, "CODIGO-ERRADO-5555", "127.0.0.1");
        Assert.False(actFail5.Success);

        var tInvalidated = await db.Tecnicos.FindAsync(tecnico.Id);
        Assert.Null(tInvalidated!.CodigoAtivacaoHash); // Código removido/invalidado!
    }

    [Fact]
    public async Task ActivationCode_ExpiredAfter24Hours_IsRejected()
    {
        var (authService, _, _, _, activationService, db) = CreateTestServices();

        var username = $"tecnico_expirado_{Guid.NewGuid():N}";
        var activationCode = activationService.GenerateActivationCode();

        var tecnico = new Tecnico
        {
            UsuarioDominio = username,
            NomeExibicao = "Técnico Código Expirado",
            Ativo = true,
            Permissoes = Permissoes.SuporteRemoto,
            CodigoAtivacaoHash = activationService.HashActivationCode(activationCode),
            CodigoAtivacaoCriadoEm = DateTimeOffset.UtcNow.AddHours(-25), // Mais de 24h
            CodigoAtivacaoTentativas = 0,
            TotpConfigurado = false
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();

        var result = await authService.ValidateActivationCodeAndBegin2FaSetupAsync(tecnico.Id, activationCode, "127.0.0.1");
        Assert.False(result.Success);
        Assert.Contains("expirado", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Totp_AntiReplay_PreventsCodeReuse_AndLocksAccountAfterFiveConsecutiveErrors()
    {
        var (authService, _, _, totpService, _, db) = CreateTestServices();

        var username = $"tecnico_totp_{Guid.NewGuid():N}";
        var plainSecret = totpService.GenerateSecret();

        var tecnico = new Tecnico
        {
            UsuarioDominio = username,
            NomeExibicao = "Técnico TOTP Teste",
            Ativo = true,
            Permissoes = Permissoes.SuporteRemoto,
            TotpSecretCifrado = totpService.EncryptSecret(plainSecret),
            TotpConfigurado = true,
            TentativasFalhas = 0
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();

        // 1. Gerar código válido e usar com sucesso
        var currentCode = GenerateCurrentTotpCode(plainSecret);
        var successResult = await authService.Validate2FaLoginAsync(tecnico.Id, currentCode, "127.0.0.1");
        Assert.True(successResult.Success);
        Assert.NotNull(successResult.Principal);

        // 2. Tentar reutilizar o MESMO código no mesmo intervalo de tempo -> Rejeitado!
        var replayResult = await authService.Validate2FaLoginAsync(tecnico.Id, currentCode, "127.0.0.1");
        Assert.False(replayResult.Success);
        Assert.Contains("reuso", replayResult.Error, StringComparison.OrdinalIgnoreCase);

        // 3. 5 falhas consecutivas de 2FA travam a conta por 15 minutos
        for (int i = 0; i < 4; i++)
        {
            var failResult = await authService.Validate2FaLoginAsync(tecnico.Id, "999999", "127.0.0.1");
            Assert.False(failResult.Success);
        }

        var tCheck = await db.Tecnicos.FindAsync(tecnico.Id);
        Assert.NotNull(tCheck!.TravadoAte);
        Assert.True(tCheck.TravadoAte > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AdRecheck_WhenInactiveInAd_TerminatesSession()
    {
        var services = new ServiceCollection();
        var db = new GtViewerDbContext(CreateOptions());
        var mockLdap = new MockLdapService();
        var auditoria = new AuditoriaService(db);

        services.AddSingleton(db);
        services.AddSingleton<ILdapService>(mockLdap);
        services.AddSingleton<IAuditoriaService>(auditoria);
        services.AddLogging();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        var sp = services.BuildServiceProvider();

        var username = $"tecnico_desativado_ad_{Guid.NewGuid():N}";
        var tecnico = new Tecnico
        {
            UsuarioDominio = username,
            NomeExibicao = "Tecnico Para Desativar",
            Ativo = true,
            Permissoes = Permissoes.SuporteRemoto
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();

        // Configura AD para retornar conta desativada
        mockLdap.CheckStatusHandler = u => new LdapUserStatus(IsActive: false, Exists: true);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim("IsContaEmergencia", "False"),
            new Claim("LastAdRecheckUtc", DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds().ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var authScheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var cookieOptions = new CookieAuthenticationOptions();
        var ticket = new AuthenticationTicket(principal, authScheme.Name);

        var context = new CookieValidatePrincipalContext(httpContext, authScheme, cookieOptions, ticket);
        var recheckEvents = new AdSessionRecheckEvents();

        await recheckEvents.ValidatePrincipal(context);

        // Confirma que a sessão foi rejeitada
        Assert.Null(context.Principal);

        // Confirma auditoria
        var audit = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.EncerrarSessaoDesativacaoAd && a.Ator == username)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task AdRecheck_WhenAdUnreachable_MaintainsSessionAndAudits()
    {
        var services = new ServiceCollection();
        var db = new GtViewerDbContext(CreateOptions());
        var mockLdap = new MockLdapService();
        var auditoria = new AuditoriaService(db);

        services.AddSingleton(db);
        services.AddSingleton<ILdapService>(mockLdap);
        services.AddSingleton<IAuditoriaService>(auditoria);
        services.AddLogging();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        var sp = services.BuildServiceProvider();

        var username = $"tecnico_ad_queda_{Guid.NewGuid():N}";
        var tecnico = new Tecnico
        {
            UsuarioDominio = username,
            NomeExibicao = "Tecnico AD Queda",
            Ativo = true,
            Permissoes = Permissoes.SuporteRemoto
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();

        // Configura AD simulando queda de rede / inacessível
        mockLdap.CheckStatusHandler = u => new LdapUserStatus(IsActive: false, Exists: false, IsConnectionError: true);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim("IsContaEmergencia", "False"),
            new Claim("LastAdRecheckUtc", DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds().ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var authScheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var cookieOptions = new CookieAuthenticationOptions();
        var ticket = new AuthenticationTicket(principal, authScheme.Name);

        var context = new CookieValidatePrincipalContext(httpContext, authScheme, cookieOptions, ticket);
        var recheckEvents = new AdSessionRecheckEvents();

        await recheckEvents.ValidatePrincipal(context);

        // Invariante e Tarefa 6: AD inacessível na reconferência -> a sessão continua (até 8h)
        Assert.NotNull(context.Principal);

        // Confirma auditoria de falha na reconferência
        var audit = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.FalhaReconferenciaAd)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task EmergencyAccount_OnlyEntersWhenEnabled_ExpiresIn24Hours_AndCreatesAlert()
    {
        var (authService, _, _, totpService, activationService, db) = CreateTestServices();
        var cliService = new ServerCliService(db, activationService, totpService, new AuditoriaService(db));

        // 1. Configurar conta de emergência
        await cliService.SetupEmergencyAccountAsync("SenhaDeEmergenciaMuitoForte123!@#");

        // Fica desativada por padrão!
        var loginAttemptDisabled = await authService.AuthenticateInitialAsync("emergencia", "SenhaDeEmergenciaMuitoForte123!@#", "127.0.0.1");
        Assert.False(loginAttemptDisabled.Success);

        // 2. Habilitar por 24 horas via CLI
        await cliService.EnableEmergencyAccountAsync(24);

        // Agora login inicial deve funcionar e exigir 2FA
        var loginAttemptEnabled = await authService.AuthenticateInitialAsync("emergencia", "SenhaDeEmergenciaMuitoForte123!@#", "127.0.0.1");
        Assert.True(loginAttemptEnabled.Success);
        Assert.True(loginAttemptEnabled.Requires2Fa);

        var emergencyTecnico = await db.Tecnicos.FirstAsync(t => t.UsuarioDominio == "emergencia");
        Assert.True(emergencyTecnico.IsContaEmergencia);
        // Permissões limitadas estritamente a 3
        Assert.Equal("bloquear,reiniciar,suporte_remoto", emergencyTecnico.Permissoes);
        Assert.False(Permissoes.IsAdmin(emergencyTecnico.Permissoes));

        // 3. Concluir login com 2FA
        var plainSecret = totpService.DecryptSecret(emergencyTecnico.TotpSecretCifrado!);
        var code = GenerateCurrentTotpCode(plainSecret);
        var login2Fa = await authService.Validate2FaLoginAsync(emergencyTecnico.Id, code, "127.0.0.1");

        Assert.True(login2Fa.Success);

        // 4. Invariante 6 e Tarefa 6: Cada login da conta de emergência gera alerta persistido
        var alerta = await db.AlertasEmergencia
            .Where(a => a.Ator == "emergencia")
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(alerta);
        Assert.False(alerta.Reconhecido);

        // 5. Expirar após 24h
        emergencyTecnico.EmergenciaExpiraEm = DateTimeOffset.UtcNow.AddHours(-1); // Expirada
        await db.SaveChangesAsync();

        var loginExpired = await authService.AuthenticateInitialAsync("emergencia", "SenhaDeEmergenciaMuitoForte123!@#", "127.0.0.1");
        Assert.False(loginExpired.Success);
    }

    [Fact]
    public async Task CreateFirstAdminCli_CreatesAdministrator_AndGeneratesActivationCode()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var activation = new ActivationCodeService();
        var totp = new TotpService(new EphemeralDataProtectionProvider());
        var auditoria = new AuditoriaService(db);
        var cliService = new ServerCliService(db, activation, totp, auditoria);

        var adminUser = $"admin_cli_{Guid.NewGuid():N}";
        var output = await cliService.CreateFirstAdminAsync(adminUser, "Primeiro Administrador", "admin@empresa.local");

        Assert.Contains("[SUCESSO]", output);

        var adminTecnico = await db.Tecnicos.FirstOrDefaultAsync(t => t.UsuarioDominio == adminUser);
        Assert.NotNull(adminTecnico);
        Assert.True(adminTecnico.Ativo);
        Assert.True(Permissoes.IsAdmin(adminTecnico.Permissoes));
        Assert.NotNull(adminTecnico.CodigoAtivacaoHash);

        var audit = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.CriarPrimeiroAdministradorCli && a.Alvo == adminUser)
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);
    }

    [Fact]
    public void LdapFilterEscaping_PreventsInjection()
    {
        var injectionAttempt = "admin*)(|(password=*";
        var escaped = LdapService.EscapeLdapFilter(injectionAttempt);

        Assert.DoesNotContain("*", escaped);
        Assert.DoesNotContain("(", escaped);
        Assert.DoesNotContain(")", escaped);
        Assert.Equal(@"admin\2a\29\28|\28password=\2a", escaped);
    }

    private static string GenerateCurrentTotpCode(string plainSecret)
    {
        var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new List<byte>();
        int buffer = 0, bitsLeft = 0;
        foreach (var c in plainSecret.Trim().ToUpperInvariant())
        {
            var val = alphabet.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                key.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var stepBytes = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(stepBytes);

        using var hmac = new System.Security.Cryptography.HMACSHA1(key.ToArray());
        var hash = hmac.ComputeHash(stepBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) |
                     ((hash[offset + 1] & 0xFF) << 16) |
                     ((hash[offset + 2] & 0xFF) << 8) |
                     (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6");
    }
}
