using System.Diagnostics;
using System.Security.Claims;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Services;

public record LoginInitialResult(
    bool Success,
    bool RequiresActivation = false,
    bool Requires2Fa = false,
    int TecnicoId = 0,
    string? Username = null,
    string? Error = null);

public record ActivationResult(
    bool Success,
    string? PlainSecret = null,
    string? QrCodeSvg = null,
    string? Uri = null,
    string? Error = null);

public record Login2FaResult(
    bool Success,
    ClaimsPrincipal? Principal = null,
    string? Error = null);

public interface IAuthService
{
    Task<LoginInitialResult> AuthenticateInitialAsync(string username, string password, string clientIp, CancellationToken cancellationToken = default);
    Task<ActivationResult> ValidateActivationCodeAndBegin2FaSetupAsync(int tecnicoId, string activationCode, string clientIp, CancellationToken cancellationToken = default);
    Task<Login2FaResult> Confirm2FaSetupAsync(int tecnicoId, string plainSecret, string totpCode, string clientIp, CancellationToken cancellationToken = default);
    Task<Login2FaResult> Validate2FaLoginAsync(int tecnicoId, string totpCode, string clientIp, CancellationToken cancellationToken = default);
    ClaimsPrincipal CreateClaimsPrincipal(Tecnico tecnico);
}

public class AuthService : IAuthService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly ILdapService _ldapService;
    private readonly ITotpService _totpService;
    private readonly IActivationCodeService _activationCodeService;
    private readonly ILoginSecurityService _securityService;
    private readonly IAuditoriaService _auditoriaService;
    private readonly PasswordHasher<Tecnico> _passwordHasher = new();

    public AuthService(
        GtViewerDbContext dbContext,
        ILdapService ldapService,
        ITotpService totpService,
        IActivationCodeService activationCodeService,
        ILoginSecurityService securityService,
        IAuditoriaService auditoriaService)
    {
        _dbContext = dbContext;
        _ldapService = ldapService;
        _totpService = totpService;
        _activationCodeService = activationCodeService;
        _securityService = securityService;
        _auditoriaService = auditoriaService;
    }

    public async Task<LoginInitialResult> AuthenticateInitialAsync(string username, string password, string clientIp, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 1. Verificar bloqueio do IP real (mais de 10 tentativas falhas/min)
            if (_securityService.IsIpBlocked(clientIp))
            {
                await _auditoriaService.RegistrarAsync(username, TiposAuditoria.LoginFalho, "gt_viewer", false, "IP bloqueado por excesso de falhas", clientIp, cancellationToken: cancellationToken);
                return new LoginInitialResult(false, Error: "Credenciais inválidas.");
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _securityService.RecordFailedAttempt(username, clientIp);
                await _auditoriaService.RegistrarAsync(username, TiposAuditoria.LoginFalho, "gt_viewer", false, "Usuário ou senha vazios", clientIp, cancellationToken: cancellationToken);
                return new LoginInitialResult(false, Error: "Credenciais inválidas.");
            }

            var normalizedUser = username.Trim().ToLowerInvariant();

            // 2. Conferir PRIMEIRO se o usuário está cadastrado e ativo no gt_viewer (J1 passo 2)
            // Se NÃO estiver cadastrado ou ativo, NUNCA consulta o AD para não travar contas de domínio
            var tecnico = await _dbContext.Tecnicos.FirstOrDefaultAsync(t => t.UsuarioDominio.ToLower() == normalizedUser, cancellationToken);
            if (tecnico == null || !tecnico.Ativo)
            {
                _securityService.RecordFailedAttempt(normalizedUser, clientIp);
                await _securityService.ApplyProgressiveDelayAsync(normalizedUser, clientIp, cancellationToken);
                await _auditoriaService.RegistrarAsync(username, TiposAuditoria.LoginFalho, "gt_viewer", false, "Usuário não cadastrado ou inativo no gt_viewer", clientIp, cancellationToken: cancellationToken);
                return new LoginInitialResult(false, Error: "Credenciais inválidas.");
            }

            // 3. Verificar travamento da conta por 2FA consecutivo (15 minutos)
            if (tecnico.TravadoAte.HasValue && DateTimeOffset.UtcNow < tecnico.TravadoAte.Value)
            {
                _securityService.RecordFailedAttempt(normalizedUser, clientIp);
                await _securityService.ApplyProgressiveDelayAsync(normalizedUser, clientIp, cancellationToken);
                await _auditoriaService.RegistrarAsync(username, TiposAuditoria.LoginFalho, "gt_viewer", false, "Conta temporariamente travada por falhas consecutivas de 2FA", clientIp, cancellationToken: cancellationToken);
                return new LoginInitialResult(false, Error: "Credenciais inválidas.");
            }

            // 4. Se for Conta de Emergência (Invariante 6 e J1):
            if (tecnico.IsContaEmergencia)
            {
                // Verificar se a conta expirou (24 horas)
                if (tecnico.EmergenciaExpiraEm.HasValue && DateTimeOffset.UtcNow > tecnico.EmergenciaExpiraEm.Value)
                {
                    tecnico.Ativo = false;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await _auditoriaService.RegistrarAsync("sistema", TiposAuditoria.HabilitarExpirarContaEmergencia, "gt_viewer", false, "Conta de emergência desativada automaticamente após 24h", clientIp, cancellationToken: cancellationToken);
                    _securityService.RecordFailedAttempt(normalizedUser, clientIp);
                    return new LoginInitialResult(false, Error: "Credenciais inválidas.");
                }

                // Autenticação local por hash de senha
                if (string.IsNullOrWhiteSpace(tecnico.SenhaLocalHash))
                {
                    _securityService.RecordFailedAttempt(normalizedUser, clientIp);
                    return new LoginInitialResult(false, Error: "Credenciais inválidas.");
                }

                var verifyResult = _passwordHasher.VerifyHashedPassword(tecnico, tecnico.SenhaLocalHash, password);
                if (verifyResult == PasswordVerificationResult.Failed)
                {
                    _securityService.RecordFailedAttempt(normalizedUser, clientIp);
                    await _securityService.ApplyProgressiveDelayAsync(normalizedUser, clientIp, cancellationToken);
                    await _auditoriaService.RegistrarAsync(username, TiposAuditoria.LoginFalho, "gt_viewer", false, "Senha incorreta da conta de emergência", clientIp, cancellationToken: cancellationToken);
                    return new LoginInitialResult(false, Error: "Credenciais inválidas.");
                }
            }
            else
            {
                // 5. Usuário regular: aplicar atraso progressivo por (usuário, IP) antes do bind no AD
                await _securityService.ApplyProgressiveDelayAsync(normalizedUser, clientIp, cancellationToken);

                // Consulta ao AD via LDAPS com validação de certificado
                var ldapResult = await _ldapService.AuthenticateAsync(normalizedUser, password, cancellationToken);
                if (!ldapResult.Success)
                {
                    _securityService.RecordFailedAttempt(normalizedUser, clientIp);
                    await _auditoriaService.RegistrarAsync(username, TiposAuditoria.LoginFalho, "gt_viewer", false, ldapResult.ErrorMessage ?? "Falha de autenticação no AD", clientIp, cancellationToken: cancellationToken);
                    return new LoginInitialResult(false, Error: "Credenciais inválidas.");
                }
            }

            // Senha correta!
            // Próxima etapa: verificar se precisa de código de ativação ou direto 2FA
            if (!tecnico.TotpConfigurado)
            {
                return new LoginInitialResult(true, RequiresActivation: true, TecnicoId: tecnico.Id, Username: tecnico.UsuarioDominio);
            }

            return new LoginInitialResult(true, Requires2Fa: true, TecnicoId: tecnico.Id, Username: tecnico.UsuarioDominio);
        }
        finally
        {
            // Garantia de tempo de resposta mínimo constante para evitar enumeração de técnicos
            await _securityService.EnsureMinimumResponseTimeAsync(stopwatch, 500, cancellationToken);
        }
    }

    public async Task<ActivationResult> ValidateActivationCodeAndBegin2FaSetupAsync(int tecnicoId, string activationCode, string clientIp, CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos.FindAsync([tecnicoId], cancellationToken);
        if (tecnico == null || !tecnico.Ativo)
        {
            return new ActivationResult(false, Error: "Código de ativação inválido.");
        }

        // Validação de expiração do código de ativação (válido por 24 horas)
        var isExpired = !tecnico.CodigoAtivacaoCriadoEm.HasValue ||
                        (DateTimeOffset.UtcNow - tecnico.CodigoAtivacaoCriadoEm.Value) > TimeSpan.FromHours(24);

        if (string.IsNullOrWhiteSpace(tecnico.CodigoAtivacaoHash) || isExpired || tecnico.CodigoAtivacaoTentativas >= 5)
        {
            // Invalida o código expirado ou que excedeu 5 tentativas
            tecnico.CodigoAtivacaoHash = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.LoginFalho, "gt_viewer", false, "Código de ativação expirado ou excedeu tentativas", clientIp, cancellationToken: cancellationToken);
            return new ActivationResult(false, Error: "Código de ativação inválido ou expirado.");
        }

        var isCodeValid = _activationCodeService.VerifyActivationCode(activationCode, tecnico.CodigoAtivacaoHash);
        if (!isCodeValid)
        {
            tecnico.CodigoAtivacaoTentativas++;
            if (tecnico.CodigoAtivacaoTentativas >= 5)
            {
                tecnico.CodigoAtivacaoHash = null; // Invalida após 5 erros
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.LoginFalho, "gt_viewer", false, $"Tentativa inválida de código de ativação ({tecnico.CodigoAtivacaoTentativas}/5)", clientIp, cancellationToken: cancellationToken);
            return new ActivationResult(false, Error: "Código de ativação inválido.");
        }

        // Código de ativação válido! Gerar segredo TOTP e QR code para configuração
        var plainSecret = _totpService.GenerateSecret();
        var uri = _totpService.GenerateTotpUri(tecnico.UsuarioDominio, plainSecret);
        var qrSvg = _totpService.GenerateQrCodeSvg(uri);

        return new ActivationResult(true, PlainSecret: plainSecret, QrCodeSvg: qrSvg, Uri: uri);
    }

    public async Task<Login2FaResult> Confirm2FaSetupAsync(int tecnicoId, string plainSecret, string totpCode, string clientIp, CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos.FindAsync([tecnicoId], cancellationToken);
        if (tecnico == null || !tecnico.Ativo)
        {
            return new Login2FaResult(false, Error: "Sessão inválida.");
        }

        if (!_totpService.ValidateCode(tecnico.Id, plainSecret, totpCode, out var failureReason))
        {
            await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.LoginFalho, "gt_viewer", false, $"Confirmação de 2FA falhou: {failureReason}", clientIp, cancellationToken: cancellationToken);
            return new Login2FaResult(false, Error: failureReason ?? "Código 2FA incorreto.");
        }

        // Sucesso na ativação do 2FA
        tecnico.TotpSecretCifrado = _totpService.EncryptSecret(plainSecret);
        tecnico.TotpConfigurado = true;
        tecnico.CodigoAtivacaoHash = null;
        tecnico.CodigoAtivacaoTentativas = 0;
        tecnico.TentativasFalhas = 0;
        tecnico.UltimoLoginEm = DateTimeOffset.UtcNow;
        tecnico.AtualizadoEm = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _securityService.RecordSuccessfulLogin(tecnico.UsuarioDominio, clientIp);
        await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.LoginOk, "gt_viewer", true, "Primeiro acesso com ativação e 2FA configurado", clientIp, cancellationToken: cancellationToken);

        var principal = CreateClaimsPrincipal(tecnico);
        return new Login2FaResult(true, Principal: principal);
    }

    public async Task<Login2FaResult> Validate2FaLoginAsync(int tecnicoId, string totpCode, string clientIp, CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos.FindAsync([tecnicoId], cancellationToken);
        if (tecnico == null || !tecnico.Ativo)
        {
            return new Login2FaResult(false, Error: "Sessão inválida.");
        }

        if (tecnico.TravadoAte.HasValue && DateTimeOffset.UtcNow < tecnico.TravadoAte.Value)
        {
            await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.LoginFalho, "gt_viewer", false, "Conta travada por 15 minutos", clientIp, cancellationToken: cancellationToken);
            return new Login2FaResult(false, Error: "Conta travada por excesso de tentativas incorretas de 2FA.");
        }

        if (string.IsNullOrWhiteSpace(tecnico.TotpSecretCifrado))
        {
            return new Login2FaResult(false, Error: "Configuração de 2FA ausente.");
        }

        var plainSecret = _totpService.DecryptSecret(tecnico.TotpSecretCifrado);
        if (!_totpService.ValidateCode(tecnico.Id, plainSecret, totpCode, out var failureReason))
        {
            tecnico.TentativasFalhas++;
            if (tecnico.TentativasFalhas >= 5)
            {
                tecnico.TravadoAte = DateTimeOffset.UtcNow.AddMinutes(15);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);

            _securityService.RecordFailedAttempt(tecnico.UsuarioDominio, clientIp);
            await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.LoginFalho, "gt_viewer", false, $"Falha 2FA: {failureReason} ({tecnico.TentativasFalhas}/5)", clientIp, cancellationToken: cancellationToken);

            return new Login2FaResult(false, Error: failureReason ?? "Código 2FA incorreto.");
        }

        // 2FA válido!
        tecnico.TentativasFalhas = 0;
        tecnico.TravadoAte = null;
        tecnico.UltimoLoginEm = DateTimeOffset.UtcNow;

        // Se for conta de emergência, registrar alerta persistente
        if (tecnico.IsContaEmergencia)
        {
            var alerta = new AlertaEmergencia
            {
                DataHora = DateTimeOffset.UtcNow,
                Ator = tecnico.UsuarioDominio,
                Mensagem = "Login efetuado utilizando a Conta de Emergência (AD fora do ar).",
                Reconhecido = false
            };
            _dbContext.AlertasEmergencia.Add(alerta);
            await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.UsoContaEmergencia, "gt_viewer", true, "Login com conta de emergência", clientIp, cancellationToken: cancellationToken);
        }
        else
        {
            await _auditoriaService.RegistrarAsync(tecnico.UsuarioDominio, TiposAuditoria.LoginOk, "gt_viewer", true, "Login efetuado com sucesso", clientIp, cancellationToken: cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _securityService.RecordSuccessfulLogin(tecnico.UsuarioDominio, clientIp);

        var principal = CreateClaimsPrincipal(tecnico);
        return new Login2FaResult(true, Principal: principal);
    }

    public ClaimsPrincipal CreateClaimsPrincipal(Tecnico tecnico)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tecnico.Id.ToString()),
            new(ClaimTypes.Name, tecnico.UsuarioDominio),
            new(ClaimTypes.GivenName, tecnico.NomeExibicao),
            new("Permissoes", tecnico.Permissoes),
            new("IsContaEmergencia", tecnico.IsContaEmergencia.ToString()),
            new("LastAdRecheckUtc", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };

        if (!string.IsNullOrWhiteSpace(tecnico.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, tecnico.Email));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}
