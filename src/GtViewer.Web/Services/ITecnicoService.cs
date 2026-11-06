using System.Text.Json;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Services;

public interface ITecnicoService
{
    Task<List<Tecnico>> ListarAsync(CancellationToken cancellationToken = default);
    Task<Tecnico?> ObterPorIdAsync(int id, CancellationToken cancellationToken = default);
    Task<Tecnico?> ObterPorUsuarioAsync(string usuarioDominio, CancellationToken cancellationToken = default);
    Task<(Tecnico Tecnico, string CodigoAtivacao)> CriarTecnicoAsync(
        string usuarioDominio,
        string nomeExibicao,
        string? email,
        IEnumerable<string> permissoes,
        string ator,
        CancellationToken cancellationToken = default);
    Task<Tecnico> AtualizarPermissoesAsync(
        int id,
        IEnumerable<string> novasPermissoes,
        string ator,
        CancellationToken cancellationToken = default);
    Task<Tecnico> DesativarTecnicoAsync(int id, string ator, CancellationToken cancellationToken = default);
    Task<Tecnico> AtivarTecnicoAsync(int id, string ator, CancellationToken cancellationToken = default);
    Task<(Tecnico Tecnico, string NovoCodigoAtivacao)> Redefinir2FaAsync(int id, string ator, CancellationToken cancellationToken = default);
    Task<int> ContarAdministradoresAtivosAsync(CancellationToken cancellationToken = default);
}

public class TecnicoService : ITecnicoService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly IActivationCodeService _activationCodeService;
    private readonly IAuditoriaService _auditoriaService;

    public TecnicoService(
        GtViewerDbContext dbContext,
        IActivationCodeService activationCodeService,
        IAuditoriaService auditoriaService)
    {
        _dbContext = dbContext;
        _activationCodeService = activationCodeService;
        _auditoriaService = auditoriaService;
    }

    public async Task<List<Tecnico>> ListarAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Tecnicos
            .OrderBy(t => t.IsContaEmergencia)
            .ThenBy(t => t.NomeExibicao)
            .ToListAsync(cancellationToken);
    }

    public async Task<Tecnico?> ObterPorIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Tecnicos.FindAsync([id], cancellationToken);
    }

    public async Task<Tecnico?> ObterPorUsuarioAsync(string usuarioDominio, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(usuarioDominio)) return null;
        var normalized = usuarioDominio.Trim().ToLowerInvariant();
        return await _dbContext.Tecnicos.FirstOrDefaultAsync(t => t.UsuarioDominio.ToLower() == normalized, cancellationToken);
    }

    public async Task<(Tecnico Tecnico, string CodigoAtivacao)> CriarTecnicoAsync(
        string usuarioDominio,
        string nomeExibicao,
        string? email,
        IEnumerable<string> permissoes,
        string ator,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(usuarioDominio))
        {
            throw new ArgumentException("O usuário de domínio é obrigatório.", nameof(usuarioDominio));
        }

        var normalizedUser = usuarioDominio.Trim().ToLowerInvariant();
        var exists = await _dbContext.Tecnicos.AnyAsync(t => t.UsuarioDominio.ToLower() == normalizedUser, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"Já existe um técnico cadastrado com o usuário '{usuarioDominio}'.");
        }

        var permList = permissoes
            .Where(p => Permissoes.Todas.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var activationCode = _activationCodeService.GenerateActivationCode();
        var codeHash = _activationCodeService.HashActivationCode(activationCode);

        var tecnico = new Tecnico
        {
            UsuarioDominio = normalizedUser,
            NomeExibicao = string.IsNullOrWhiteSpace(nomeExibicao) ? normalizedUser : nomeExibicao.Trim(),
            Email = email?.Trim(),
            Ativo = true,
            Permissoes = string.Join(",", permList),
            CodigoAtivacaoHash = codeHash,
            CodigoAtivacaoCriadoEm = DateTimeOffset.UtcNow,
            CodigoAtivacaoTentativas = 0,
            TotpConfigurado = false,
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };

        _dbContext.Tecnicos.Add(tecnico);

        // Auditoria na mesma transação (Invariante 5 e J9)
        await _auditoriaService.RegistrarAsync(
            ator,
            TiposAuditoria.CriarAlterarDesativarTecnico,
            tecnico.UsuarioDominio,
            true,
            JsonSerializer.Serialize(new { acao = "criar", permissoes = permList }),
            cancellationToken: cancellationToken);

        await _auditoriaService.RegistrarAsync(
            ator,
            TiposAuditoria.GerarCodigoAtivacao,
            tecnico.UsuarioDominio,
            true,
            "Código de ativação inicial gerado com validade de 24h",
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return (tecnico, activationCode);
    }

    public async Task<Tecnico> AtualizarPermissoesAsync(
        int id,
        IEnumerable<string> novasPermissoes,
        string ator,
        CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos.FindAsync([id], cancellationToken);
        if (tecnico == null)
        {
            throw new KeyNotFoundException($"Técnico com ID {id} não encontrado.");
        }

        if (tecnico.IsContaEmergencia)
        {
            throw new InvalidOperationException("As permissões da conta de emergência são estritas e não podem ser alteradas.");
        }

        var permList = novasPermissoes
            .Where(p => Permissoes.Todas.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var perdePermissaoAdmin = tecnico.PossuiPermissao(Permissoes.AdministrarTecnicos) &&
                                  !permList.Contains(Permissoes.AdministrarTecnicos, StringComparer.OrdinalIgnoreCase);

        if (perdePermissaoAdmin && tecnico.Ativo)
        {
            var adminCount = await ContarAdministradoresAtivosAsync(cancellationToken);
            if (adminCount <= 1)
            {
                throw new InvalidOperationException("Não é permitido remover a permissão de administrador do último administrador ativo do sistema.");
            }
        }

        tecnico.Permissoes = string.Join(",", permList);
        tecnico.AtualizadoEm = DateTimeOffset.UtcNow;

        await _auditoriaService.RegistrarAsync(
            ator,
            TiposAuditoria.CriarAlterarDesativarTecnico,
            tecnico.UsuarioDominio,
            true,
            JsonSerializer.Serialize(new { acao = "alterar_permissoes", permissoes = permList }),
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return tecnico;
    }

    public async Task<Tecnico> DesativarTecnicoAsync(int id, string ator, CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos.FindAsync([id], cancellationToken);
        if (tecnico == null)
        {
            throw new KeyNotFoundException($"Técnico com ID {id} não encontrado.");
        }

        if (tecnico.IsContaEmergencia)
        {
            throw new InvalidOperationException("A conta de emergência é gerenciada pelo ciclo de vida de expiração de 24 horas via CLI.");
        }

        // Proteção do último administrador (J9 e Tarefa 7)
        if (tecnico.Ativo && tecnico.PossuiPermissao(Permissoes.AdministrarTecnicos))
        {
            var adminCount = await ContarAdministradoresAtivosAsync(cancellationToken);
            if (adminCount <= 1)
            {
                throw new InvalidOperationException("Não é permitido desativar o último administrador ativo do sistema.");
            }
        }

        tecnico.Ativo = false;
        tecnico.AtualizadoEm = DateTimeOffset.UtcNow;

        // Ao desativar, revogar qualquer trava de suporte remoto ativa do técnico
        var trava = await _dbContext.TravasSessao
            .FirstOrDefaultAsync(t => t.TecnicoUsername.ToLower() == tecnico.UsuarioDominio.ToLower(), cancellationToken);
        if (trava != null)
        {
            _dbContext.TravasSessao.Remove(trava);
        }

        await _auditoriaService.RegistrarAsync(
            ator,
            TiposAuditoria.CriarAlterarDesativarTecnico,
            tecnico.UsuarioDominio,
            true,
            JsonSerializer.Serialize(new { acao = "desativar" }),
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return tecnico;
    }

    public async Task<Tecnico> AtivarTecnicoAsync(int id, string ator, CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos.FindAsync([id], cancellationToken);
        if (tecnico == null)
        {
            throw new KeyNotFoundException($"Técnico com ID {id} não encontrado.");
        }

        tecnico.Ativo = true;
        tecnico.TentativasFalhas = 0;
        tecnico.TravadoAte = null;
        tecnico.AtualizadoEm = DateTimeOffset.UtcNow;

        await _auditoriaService.RegistrarAsync(
            ator,
            TiposAuditoria.CriarAlterarDesativarTecnico,
            tecnico.UsuarioDominio,
            true,
            JsonSerializer.Serialize(new { acao = "ativar" }),
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return tecnico;
    }

    public async Task<(Tecnico Tecnico, string NovoCodigoAtivacao)> Redefinir2FaAsync(int id, string ator, CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos.FindAsync([id], cancellationToken);
        if (tecnico == null)
        {
            throw new KeyNotFoundException($"Técnico com ID {id} não encontrado.");
        }

        var novoCodigo = _activationCodeService.GenerateActivationCode();
        var codeHash = _activationCodeService.HashActivationCode(novoCodigo);

        tecnico.TotpConfigurado = false;
        tecnico.TotpSecretCifrado = null;
        tecnico.CodigoAtivacaoHash = codeHash;
        tecnico.CodigoAtivacaoCriadoEm = DateTimeOffset.UtcNow;
        tecnico.CodigoAtivacaoTentativas = 0;
        tecnico.TentativasFalhas = 0;
        tecnico.TravadoAte = null;
        tecnico.AtualizadoEm = DateTimeOffset.UtcNow;

        await _auditoriaService.RegistrarAsync(
            ator,
            TiposAuditoria.Redefinir2Fa,
            tecnico.UsuarioDominio,
            true,
            "2FA redefinido pelo administrador; gerado novo código de ativação",
            cancellationToken: cancellationToken);

        await _auditoriaService.RegistrarAsync(
            ator,
            TiposAuditoria.GerarCodigoAtivacao,
            tecnico.UsuarioDominio,
            true,
            "Novo código de ativação gerado para redefinição de 2FA",
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return (tecnico, novoCodigo);
    }

    public async Task<int> ContarAdministradoresAtivosAsync(CancellationToken cancellationToken = default)
    {
        var tecnicosAtivos = await _dbContext.Tecnicos
            .Where(t => t.Ativo && !t.IsContaEmergencia)
            .ToListAsync(cancellationToken);

        return tecnicosAtivos.Count(t => t.PossuiPermissao(Permissoes.AdministrarTecnicos));
    }
}
