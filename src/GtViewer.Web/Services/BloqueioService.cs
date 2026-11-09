using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GtViewer.Web.Services;

public class BloqueioService : IBloqueioService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly IFilaAcoesService _filaAcoesService;
    private readonly IRescuePasswordService _rescuePasswordService;
    private readonly ITotpService _totpService;
    private readonly IAuditoriaService _auditoriaService;
    private readonly IMeshCentralClient _meshClient;
    private readonly ILogger<BloqueioService> _logger;

    public BloqueioService(
        GtViewerDbContext dbContext,
        IFilaAcoesService filaAcoesService,
        IRescuePasswordService rescuePasswordService,
        ITotpService totpService,
        IAuditoriaService auditoriaService,
        IMeshCentralClient meshClient,
        ILogger<BloqueioService> logger)
    {
        _dbContext = dbContext;
        _filaAcoesService = filaAcoesService;
        _rescuePasswordService = rescuePasswordService;
        _totpService = totpService;
        _auditoriaService = auditoriaService;
        _meshClient = meshClient;
        _logger = logger;
    }

    public async Task SolicitarBloqueioAsync(
        int maquinaId,
        string tecnicoUsername,
        string motivo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(motivo))
        {
            throw new ArgumentException("O motivo do bloqueio é obrigatório.", nameof(motivo));
        }

        var tecnico = await _dbContext.Tecnicos
            .FirstOrDefaultAsync(t => t.UsuarioDominio == tecnicoUsername, cancellationToken);

        if (tecnico == null || !tecnico.PossuiPermissao(Permissoes.Bloquear))
        {
            throw new UnauthorizedAccessException("Técnico não possui permissão para bloquear máquinas.");
        }

        var maquina = await _dbContext.Maquinas
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        if (maquina.BloqueioEstado is "bloqueada" or "bloqueio_pendente")
        {
            throw new InvalidOperationException("A máquina já se encontra bloqueada ou com bloqueio pendente.");
        }

        // Pré-condição (seção 2.5): a conta de resgate deve ter senha confirmada no banco
        if (string.IsNullOrEmpty(maquina.SenhaResgateAtualCifrada))
        {
            throw new InvalidOperationException("O bloqueio só pode ser aplicado após a confirmação da senha da conta de resgate.");
        }

        maquina.BloqueioEstado = "bloqueio_pendente";
        maquina.MensagemBloqueio = motivo.Trim();
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Enfileira a ação com prioridade 2 (Bloqueio) e auditoria atômica
        await _filaAcoesService.EnfileirarAcaoAsync(
            maquina.Id,
            TiposAcao.Bloquear,
            tecnicoUsername,
            JsonSerializer.Serialize(new { motivo = motivo.Trim() }),
            prioridade: PrioridadesAcao.Bloqueio,
            cancellationToken: cancellationToken);

        _logger.LogInformation("[BloqueioService] Bloqueio solicitado para a máquina {Serie} por {Tecnico}. Motivo: {Motivo}",
            maquina.NumeroSerie, tecnicoUsername, motivo);
    }

    public async Task SolicitarDesbloqueioAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos
            .FirstOrDefaultAsync(t => t.UsuarioDominio == tecnicoUsername, cancellationToken);

        if (tecnico == null || !tecnico.PossuiPermissao(Permissoes.Bloquear))
        {
            throw new UnauthorizedAccessException("Técnico não possui permissão para desbloquear máquinas.");
        }

        var maquina = await _dbContext.Maquinas
            .Include(m => m.Acoes)
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        // Caso especial 1: Desbloquear com ação de bloqueio ainda 'pendente' -> apenas cancela a pendência (spec J6)
        var acaoBloqueioPendente = maquina.Acoes
            .FirstOrDefault(a => a.Tipo == TiposAcao.Bloquear && a.Estado == EstadosAcao.Pendente);

        if (acaoBloqueioPendente != null)
        {
            await _filaAcoesService.CancelarAcaoAsync(
                acaoBloqueioPendente.Id,
                tecnicoUsername,
                "Desbloqueio solicitado antes do envio do bloqueio para a máquina.",
                cancellationToken);

            maquina.BloqueioEstado = "desbloqueada";
            maquina.MensagemBloqueio = null;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("[BloqueioService] Bloqueio pendente da máquina {Serie} cancelado antes do envio.", maquina.NumeroSerie);
            return;
        }

        // Caso especial 2, 3 e 4: Bloqueio já enviado, em resultado_desconhecido, máquina bloqueada ou em falha
        maquina.BloqueioEstado = "desbloqueio_pendente";
        maquina.MensagemBloqueio = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _filaAcoesService.EnfileirarAcaoAsync(
            maquina.Id,
            TiposAcao.Desbloquear,
            tecnicoUsername,
            JsonSerializer.Serialize(new { acao = "solicitacao_desbloqueio" }),
            prioridade: PrioridadesAcao.Bloqueio,
            cancellationToken: cancellationToken);

        _logger.LogInformation("[BloqueioService] Desbloqueio enfileirado para a máquina {Serie} por {Tecnico}.",
            maquina.NumeroSerie, tecnicoUsername);
    }

    public bool PodeRevelarSenha(Maquina maquina, out string? motivoIndisponivel)
    {
        // 1. Conexão com o MeshCentral ativa
        if (!_meshClient.IsConnected)
        {
            motivoIndisponivel = "Conexão com o servidor de máquinas (MeshCentral) indisponível.";
            return false;
        }

        // 2. Estado de bloqueio válido
        var estadoValido = maquina.BloqueioEstado is "bloqueada" or "desbloqueio_pendente" or "falha";

        if (maquina.BloqueioEstado == "bloqueio_pendente")
        {
            // Permitido apenas se a ação já foi enviada ou está em resultado_desconhecido
            var acaoBloqueioEnviadaOuDesconhecida = maquina.Acoes.Any(a =>
                a.Tipo == TiposAcao.Bloquear &&
                (a.Estado == EstadosAcao.Enviada || a.Estado == EstadosAcao.ResultadoDesconhecido));

            if (acaoBloqueioEnviadaOuDesconhecida)
            {
                estadoValido = true;
            }
            else
            {
                motivoIndisponivel = "A máquina ainda não foi fisicamente bloqueada (ação de bloqueio ainda pendente na fila).";
                return false;
            }
        }

        if (!estadoValido)
        {
            motivoIndisponivel = "A máquina está desbloqueada. Revelação permitida apenas para equipamentos bloqueados ou em falha.";
            return false;
        }

        // 3. Máquina offline há mais de 5 minutos
        var isOnline = string.Equals(maquina.ConexaoEstado, "online", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(maquina.NodeId) && _meshClient.CachedNodes.TryGetValue(maquina.NodeId, out var node))
        {
            var conn = (node["conn"]?.GetValue<int>() ?? 0) & 1;
            if (conn != 0) isOnline = true;
        }

        if (isOnline)
        {
            motivoIndisponivel = "A máquina está online. A revelação é restrita a computadores offline há mais de 5 minutos.";
            return false;
        }

        var ultimoContato = maquina.UltimoContatoEm ?? DateTimeOffset.MinValue;
        if (DateTimeOffset.UtcNow - ultimoContato <= TimeSpan.FromMinutes(5))
        {
            motivoIndisponivel = "O equipamento precisa estar offline há mais de 5 minutos para revelar a senha de resgate.";
            return false;
        }

        motivoIndisponivel = null;
        return true;
    }

    public async Task<string> RevelarSenhaResgateAsync(
        int maquinaId,
        string tecnicoUsername,
        string totpCode,
        CancellationToken cancellationToken = default)
    {
        var tecnico = await _dbContext.Tecnicos
            .FirstOrDefaultAsync(t => t.UsuarioDominio == tecnicoUsername, cancellationToken);

        if (tecnico == null || !tecnico.PossuiPermissao(Permissoes.RevelarSenhaResgate))
        {
            throw new UnauthorizedAccessException("Técnico não possui permissão para revelar a senha de resgate.");
        }

        var maquina = await _dbContext.Maquinas
            .Include(m => m.Acoes)
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        if (!PodeRevelarSenha(maquina, out var motivoIndisponivel))
        {
            throw new InvalidOperationException(motivoIndisponivel);
        }

        // Validação estrita de novo código TOTP do técnico solicitante
        if (string.IsNullOrEmpty(tecnico.TotpSecretCifrado))
        {
            throw new InvalidOperationException("Técnico não possui segundo fator (2FA/TOTP) configurado.");
        }

        var secret = _totpService.DecryptSecret(tecnico.TotpSecretCifrado);
        if (!_totpService.ValidateCode(tecnico.Id, secret, totpCode, out var failureReason))
        {
            // Grava auditoria de tentativa falha
            await _auditoriaService.RegistrarAsync(
                ator: tecnicoUsername,
                tipo: TiposAuditoria.RevelarSenhaResgate,
                alvo: maquina.NumeroSerie,
                sucesso: false,
                detalhesJson: JsonSerializer.Serialize(new { erro = failureReason }),
                maquinaId: maquina.Id,
                cancellationToken: cancellationToken);

            throw new InvalidOperationException($"Código do autenticador inválido: {failureReason}");
        }

        // Marca a senha como revelada e grava auditoria de sucesso (SEM SEGREDO)
        maquina.SenhaResgateRevelada = true;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditoriaService.RegistrarAsync(
            ator: tecnicoUsername,
            tipo: TiposAuditoria.RevelarSenhaResgate,
            alvo: maquina.NumeroSerie,
            sucesso: true,
            detalhesJson: JsonSerializer.Serialize(new { motivo = "Senha revelada para acesso emergencial por telefone" }),
            maquinaId: maquina.Id,
            cancellationToken: cancellationToken);

        _logger.LogWarning("[BloqueioService] Senha de resgate da máquina {Serie} revelada pelo técnico {Tecnico}.",
            maquina.NumeroSerie, tecnicoUsername);

        // Decifra a senha de resgate para exibição ao técnico
        var encryptedPassword = maquina.SenhaResgateAtualCifrada ?? maquina.SenhaResgateCandidataCifrada;
        if (string.IsNullOrEmpty(encryptedPassword))
        {
            throw new InvalidOperationException("Nenhuma senha de resgate registrada para esta máquina.");
        }

        return _rescuePasswordService.DecryptPassword(encryptedPassword);
    }

    public async Task AgendarTrocaSenhaResgateAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default)
    {
        var maquina = await _dbContext.Maquinas
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        if (!string.IsNullOrEmpty(maquina.SenhaResgateCandidataCifrada))
        {
            throw new InvalidOperationException("Não se inicia nova troca enquanto houver candidata não resolvida.");
        }

        var novaSenha = _rescuePasswordService.GeneratePassword();
        maquina.SenhaResgateCandidataCifrada = _rescuePasswordService.EncryptPassword(novaSenha);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _filaAcoesService.EnfileirarAcaoAsync(
            maquina.Id,
            TiposAcao.TrocarResgate,
            tecnicoUsername,
            JsonSerializer.Serialize(new { acao = "trocar_senha_resgate" }),
            prioridade: PrioridadesAcao.Resgate,
            cancellationToken: cancellationToken);

        _logger.LogInformation("[BloqueioService] Troca de senha agendada para a máquina {Serie}.", maquina.NumeroSerie);
    }

    public async Task AgendarRedefinirResgateAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default)
    {
        var maquina = await _dbContext.Maquinas
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        var novaSenha = _rescuePasswordService.GeneratePassword();
        maquina.SenhaResgateCandidataCifrada = _rescuePasswordService.EncryptPassword(novaSenha);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _filaAcoesService.EnfileirarAcaoAsync(
            maquina.Id,
            TiposAcao.RedefinirResgate,
            tecnicoUsername,
            JsonSerializer.Serialize(new { acao = "redefinir_conta_resgate" }),
            prioridade: PrioridadesAcao.Resgate,
            cancellationToken: cancellationToken);

        _logger.LogInformation("[BloqueioService] Redefinição de conta de resgate agendada para a máquina {Serie}.", maquina.NumeroSerie);
    }

    public async Task<string?> ObterSenhaAgentePorTokenAsync(
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return null;

        var tokenHash = ComputeSha256(bearerToken.Trim());

        var acao = await _dbContext.Acoes
            .Include(a => a.Maquina)
            .FirstOrDefaultAsync(a => a.TokenAgenteHash == tokenHash, cancellationToken);

        if (acao == null) return null;

        // Token expirado?
        if (acao.TokenAgenteExpiraEm.HasValue && DateTimeOffset.UtcNow > acao.TokenAgenteExpiraEm.Value)
        {
            _logger.LogWarning("[BloqueioService] Tentativa de uso de token expirado para a ação {AcaoId}.", acao.Id);
            return null;
        }

        // Token já utilizado? (Uso único)
        if (acao.TokenAgenteUsado)
        {
            _logger.LogWarning("[BloqueioService] Tentativa de reutilização de token da ação {AcaoId}.", acao.Id);
            return null;
        }

        acao.TokenAgenteUsado = true;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var maquina = acao.Maquina;
        if (maquina == null) return null;

        var encryptedPassword = maquina.SenhaResgateCandidataCifrada ?? maquina.SenhaResgateAtualCifrada;
        if (string.IsNullOrEmpty(encryptedPassword)) return null;

        return _rescuePasswordService.DecryptPassword(encryptedPassword);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
