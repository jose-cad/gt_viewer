using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Services;

public interface IServerCliService
{
    Task<string> CreateFirstAdminAsync(string domainUser, string displayName, string? email = null);
    Task<string> SetupEmergencyAccountAsync(string password);
    Task<string> EnableEmergencyAccountAsync(int hours = 24);
}

public class ServerCliService : IServerCliService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly IActivationCodeService _activationCodeService;
    private readonly ITotpService _totpService;
    private readonly IAuditoriaService _auditoriaService;
    private readonly PasswordHasher<Tecnico> _passwordHasher = new();

    public ServerCliService(
        GtViewerDbContext dbContext,
        IActivationCodeService activationCodeService,
        ITotpService totpService,
        IAuditoriaService auditoriaService)
    {
        _dbContext = dbContext;
        _activationCodeService = activationCodeService;
        _totpService = totpService;
        _auditoriaService = auditoriaService;
    }

    public async Task<string> CreateFirstAdminAsync(string domainUser, string displayName, string? email = null)
    {
        if (string.IsNullOrWhiteSpace(domainUser))
        {
            throw new ArgumentException("O usuário de domínio é obrigatório.", nameof(domainUser));
        }

        domainUser = domainUser.Trim().ToLowerInvariant();
        displayName = string.IsNullOrWhiteSpace(displayName) ? domainUser : displayName.Trim();

        var tecnico = await _dbContext.Tecnicos.FirstOrDefaultAsync(t => t.UsuarioDominio.ToLower() == domainUser);
        if (tecnico == null)
        {
            tecnico = new Tecnico
            {
                UsuarioDominio = domainUser,
                NomeExibicao = displayName,
                Email = email?.Trim(),
                Ativo = true,
                Permissoes = string.Join(",", Permissoes.Todas)
            };
            _dbContext.Tecnicos.Add(tecnico);
        }
        else
        {
            tecnico.NomeExibicao = displayName;
            tecnico.Ativo = true;
            tecnico.Permissoes = string.Join(",", Permissoes.Todas);
        }

        var activationCode = _activationCodeService.GenerateActivationCode();
        tecnico.CodigoAtivacaoHash = _activationCodeService.HashActivationCode(activationCode);
        tecnico.CodigoAtivacaoCriadoEm = DateTimeOffset.UtcNow;
        tecnico.CodigoAtivacaoTentativas = 0;
        tecnico.TotpConfigurado = false;
        tecnico.TotpSecretCifrado = null;

        await _dbContext.SaveChangesAsync();

        await _auditoriaService.RegistrarAsync(
            "sistema",
            TiposAuditoria.CriarPrimeiroAdministradorCli,
            domainUser,
            true,
            "Administrador configurado via comando CLI no servidor");

        await _auditoriaService.RegistrarAsync(
            "sistema",
            TiposAuditoria.GerarCodigoAtivacao,
            domainUser,
            true,
            "Código de ativação de primeiro acesso gerado com validade de 24h");

        return $@"=====================================================
[SUCESSO] Administrador '{domainUser}' configurado!
Código de ativação: {activationCode}
Validade: 24 horas (máx 5 tentativas)
Instruções: Acesse a tela de login, informe seu usuário do AD e este código para cadastrar o autenticador (TOTP).
=====================================================";
    }

    public async Task<string> SetupEmergencyAccountAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 16)
        {
            throw new ArgumentException("A senha da conta de emergência deve ter no mínimo 16 caracteres.", nameof(password));
        }

        const string user = "emergencia";
        var tecnico = await _dbContext.Tecnicos.FirstOrDefaultAsync(t => t.UsuarioDominio.ToLower() == user);
        if (tecnico == null)
        {
            tecnico = new Tecnico
            {
                UsuarioDominio = user,
                NomeExibicao = "Conta de Emergência (AD Offline)",
                Ativo = false, // Fica desativada por padrão!
                IsContaEmergencia = true,
                Permissoes = string.Join(",", Permissoes.Emergencia)
            };
            _dbContext.Tecnicos.Add(tecnico);
        }

        tecnico.IsContaEmergencia = true;
        tecnico.Ativo = false;
        tecnico.Permissoes = string.Join(",", Permissoes.Emergencia);
        tecnico.SenhaLocalHash = _passwordHasher.HashPassword(tecnico, password);

        var plainSecret = _totpService.GenerateSecret();
        tecnico.TotpSecretCifrado = _totpService.EncryptSecret(plainSecret);
        tecnico.TotpConfigurado = true;

        await _dbContext.SaveChangesAsync();

        var uri = _totpService.GenerateTotpUri("emergencia", plainSecret);

        await _auditoriaService.RegistrarAsync(
            "sistema",
            TiposAuditoria.HabilitarExpirarContaEmergencia,
            "emergencia",
            true,
            "Credenciais locais da conta de emergência configuradas no servidor");

        return $@"=====================================================
[SUCESSO] Conta de emergência 'emergencia' configurada!
Status: DESATIVADA (habilite via CLI apenas quando o AD estiver indisponível)
Permissões: {tecnico.Permissoes}
Segredo TOTP: {plainSecret}
URI TOTP: {uri}
=====================================================";
    }

    public async Task<string> EnableEmergencyAccountAsync(int hours = 24)
    {
        if (hours <= 0 || hours > 24)
        {
            hours = 24; // Máximo 24h conforme invariante 6
        }

        const string user = "emergencia";
        var tecnico = await _dbContext.Tecnicos.FirstOrDefaultAsync(t => t.UsuarioDominio.ToLower() == user);
        if (tecnico == null)
        {
            throw new InvalidOperationException("A conta de emergência ainda não foi inicializada. Execute --setup-emergency primeiro.");
        }

        tecnico.Ativo = true;
        tecnico.Permissoes = string.Join(",", Permissoes.Emergencia);
        tecnico.EmergenciaExpiraEm = DateTimeOffset.UtcNow.AddHours(hours);
        tecnico.TentativasFalhas = 0;
        tecnico.TravadoAte = null;

        await _dbContext.SaveChangesAsync();

        await _auditoriaService.RegistrarAsync(
            "sistema",
            TiposAuditoria.HabilitarExpirarContaEmergencia,
            "emergencia",
            true,
            $"Conta de emergência habilitada até {tecnico.EmergenciaExpiraEm:yyyy-MM-dd HH:mm:ss} UTC ({hours}h)");

        return $@"=====================================================
[ALERTA DE EMERGÊNCIA] Conta 'emergencia' HABILITADA!
Expira em: {tecnico.EmergenciaExpiraEm:yyyy-MM-dd HH:mm:ss} UTC (em {hours} horas)
Após esse prazo, a conta será desativada e as sessões ativas serão imediatamente encerradas.
Permissões limitadas estritamente a: {tecnico.Permissoes}
=====================================================";
    }
}
