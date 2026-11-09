using System.Text.Json;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Services;

public class SuporteRemotoService : ISuporteRemotoService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly IMeshCentralClient _meshClient;
    private readonly MeshLoginTokenService _tokenService;
    private readonly ILogger<SuporteRemotoService> _logger;

    public SuporteRemotoService(
        GtViewerDbContext dbContext,
        IMeshCentralClient meshClient,
        MeshLoginTokenService tokenService,
        ILogger<SuporteRemotoService> logger)
    {
        _dbContext = dbContext;
        _meshClient = meshClient;
        _tokenService = tokenService;
        _logger = logger;

        // Escuta eventos do MeshCentral para detecção de consentimento, recusa ou encerramento
        _meshClient.RawMessageReceived += OnRawMessageReceived;
    }

    public async Task<ResultadoInicioSessao> IniciarSessaoAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default)
    {
        // 1. Validação de técnico e permissão
        var tecnico = await _dbContext.Tecnicos
            .FirstOrDefaultAsync(t => t.UsuarioDominio == tecnicoUsername, cancellationToken);

        if (tecnico != null)
        {
            if (!tecnico.Ativo)
            {
                throw new UnauthorizedAccessException("Técnico inativo no sistema.");
            }
            if (!tecnico.PossuiPermissao(Permissoes.SuporteRemoto))
            {
                throw new UnauthorizedAccessException("Técnico não possui permissão para suporte remoto.");
            }
        }
        else if (tecnicoUsername != "sistema")
        {
            throw new UnauthorizedAccessException("Técnico não encontrado ou sem permissão para suporte remoto.");
        }

        // 2. Validação da conexão com o MeshCentral (J5 e Convenções)
        if (!_meshClient.IsConnected)
        {
            throw new InvalidOperationException("Sem conexão com o servidor de máquinas (MeshCentral) — suporte remoto indisponível.");
        }

        // 3. Validação da máquina e presença de usuário logado (Invariante 9)
        var maquina = await _dbContext.Maquinas
            .Include(m => m.TravaSessao)
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        if (maquina.ConexaoEstado != "online")
        {
            throw new InvalidOperationException("A máquina não está online. O suporte remoto requer conexão ativa com o equipamento.");
        }

        if (string.IsNullOrWhiteSpace(maquina.NodeId))
        {
            throw new InvalidOperationException("A máquina não possui identificador de conexão (NodeId).");
        }

        // Conforme J5 e Invariante 9: "Sempre é preciso a permissão de um usuário logado. Sem ninguém logado -> não há acesso à tela"
        var usuariosLogados = ObterUsuariosLogados(maquina.UsuariosLogadosJson);
        if (usuariosLogados.Count == 0)
        {
            throw new InvalidOperationException("Não há usuário logado no computador no momento. Conforme a política de privacidade (J5), o suporte remoto requer a presença e o consentimento de um usuário na máquina.");
        }

        var usuarioAlvo = usuariosLogados[0];
        var agora = DateTimeOffset.UtcNow;

        // 4. Exclusão mútua: verificar se a máquina já está reservada por outro técnico
        var travaExistente = maquina.TravaSessao ?? await _dbContext.TravasSessao
            .FirstOrDefaultAsync(t => t.MaquinaId == maquina.Id, cancellationToken);

        if (travaExistente != null)
        {
            // Se a trava não expirou e não ultrapassou 60 minutos
            if (travaExistente.ExpiraEm > agora && travaExistente.AdquiridaEm.AddMinutes(60) > agora)
            {
                if (!string.Equals(travaExistente.TecnicoUsername, tecnicoUsername, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Esta máquina já está em sessão de suporte com o técnico {travaExistente.TecnicoUsername}.");
                }
                else
                {
                    // Mesmo técnico reabrindo/atualizando a página: renova a trava e emite novo token
                    travaExistente.UltimoHeartbeatEm = agora;
                    travaExistente.ExpiraEm = agora.AddSeconds(90);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var mirrorUserExistente = ObterMirrorUsername(tecnicoUsername);
                    await _meshClient.EnsureMirrorUserAsync(tecnicoUsername, cancellationToken: cancellationToken);
                    await _meshClient.GrantRemoteDesktopRightsAsync(maquina.NodeId, tecnicoUsername, cancellationToken: cancellationToken);

                    var tokenRenovado = _tokenService.GenerateLoginToken(mirrorUserExistente, expireMinutes: 2);
                    var urlRenovada = _tokenService.BuildRemoteDesktopUrl(tokenRenovado, maquina.NodeId, viewmode: 11, hide: 31);

                    var acaoExistente = await _dbContext.Acoes
                        .Where(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.SuporteRemoto && a.ConcluidoEm == null)
                        .OrderByDescending(a => a.CriadoEm)
                        .FirstOrDefaultAsync(cancellationToken);

                    return new ResultadoInicioSessao(
                        travaExistente.IdentificadorSessao,
                        urlRenovada,
                        tokenRenovado,
                        mirrorUserExistente,
                        maquina.NodeId,
                        travaExistente.UsuarioLogadoNoMomento,
                        acaoExistente?.Id ?? 0);
                }
            }
            else
            {
                // Trava anterior expirada: encerra e remove antes de criar nova
                await EncerrarSessaoInternoAsync(travaExistente, "Trava anterior expirada", cancellationToken);
            }
        }

        // 5. Invariante 5: Gravação de Ação, Auditoria e TravaSessao na MESMA transação antes de qualquer efeito externo
        var sessionId = Guid.NewGuid().ToString("N");
        var mirrorUser = ObterMirrorUsername(tecnicoUsername);

        var parametrosJson = JsonSerializer.Serialize(new
        {
            sessionId,
            usuarioAlvo,
            nodeId = maquina.NodeId,
            mirrorUser
        });

        var acao = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = TiposAcao.SuporteRemoto,
            Prioridade = 0, // Ação imediata
            Estado = EstadosAcao.Enviada,
            Ator = tecnicoUsername,
            ParametrosJson = parametrosJson,
            CriadoEm = agora
        };

        var auditoria = new Auditoria
        {
            DataHora = agora,
            Ator = tecnicoUsername,
            Tipo = TiposAuditoria.AcessarTela,
            Alvo = maquina.NumeroSerie,
            MaquinaId = maquina.Id,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new { evento = "inicio", sessionId, usuarioAlvo })
        };

        var novaTrava = new TravaSessao
        {
            MaquinaId = maquina.Id,
            TecnicoUsername = tecnicoUsername,
            IdentificadorSessao = sessionId,
            UsuarioLogadoNoMomento = usuarioAlvo,
            AdquiridaEm = agora,
            UltimoHeartbeatEm = agora,
            ExpiraEm = agora.AddSeconds(90)
        };

        try
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            _dbContext.Acoes.Add(acao);
            await _dbContext.SaveChangesAsync(cancellationToken);

            auditoria.AcaoId = acao.Id;
            _dbContext.Auditorias.Add(auditoria);
            _dbContext.TravasSessao.Add(novaTrava);

            if (tecnico != null && tecnico.IsContaEmergencia)
            {
                var alerta = new AlertaEmergencia
                {
                    DataHora = agora,
                    Ator = tecnicoUsername,
                    Mensagem = $"Sessão de suporte remoto iniciada na máquina {maquina.NumeroSerie} pela conta de emergência.",
                    Reconhecido = false
                };
                _dbContext.AlertasEmergencia.Add(alerta);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message.Contains("IX_TravasSessao_MaquinaId") == true
                                           || dbEx.Message.Contains("IX_TravasSessao_MaquinaId")
                                           || dbEx.InnerException?.Message.Contains("duplicate key") == true)
        {
            // Proteção contra cliques simultâneos de técnicos diferentes concorrendo pela mesma máquina
            _logger.LogWarning("[SuporteRemoto] Concorrência detectada: máquina {Id} já travada por outro técnico.", maquina.Id);
            throw new InvalidOperationException("Esta máquina acabou de ser reservada por outro técnico. Acesso simultâneo bloqueado.");
        }

        // 6. Concessão no MeshCentral após gravação atômica da auditoria
        try
        {
            await _meshClient.EnsureMirrorUserAsync(tecnicoUsername, cancellationToken: cancellationToken);
            await _meshClient.GrantRemoteDesktopRightsAsync(maquina.NodeId, tecnicoUsername, cancellationToken: cancellationToken);

            var loginToken = _tokenService.GenerateLoginToken(mirrorUser, expireMinutes: 2);
            var remoteUrl = _tokenService.BuildRemoteDesktopUrl(loginToken, maquina.NodeId, viewmode: 11, hide: 31);

            _logger.LogInformation("[SuporteRemoto] Sessão {SessionId} iniciada para {Serie} pelo técnico {Tecnico}.",
                sessionId, maquina.NumeroSerie, tecnicoUsername);

            return new ResultadoInicioSessao(
                sessionId,
                remoteUrl,
                loginToken,
                mirrorUser,
                maquina.NodeId,
                usuarioAlvo,
                acao.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SuporteRemoto] Falha ao configurar direitos de tela no MeshCentral para {Serie}", maquina.NumeroSerie);

            // Reverte a trava em caso de falha externa
            await EncerrarSessaoInternoAsync(novaTrava, $"Falha de comunicação com MeshCentral: {ex.Message}", CancellationToken.None);
            throw new InvalidOperationException($"Falha ao inicializar direitos de suporte remoto no servidor: {ex.Message}", ex);
        }
    }

    public async Task<bool> RenovarHeartbeatAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var trava = await _dbContext.TravasSessao
            .Include(t => t.Maquina)
            .FirstOrDefaultAsync(t => t.IdentificadorSessao == sessionId, cancellationToken);

        if (trava == null)
        {
            return false;
        }

        var agora = DateTimeOffset.UtcNow;

        // 1. Limite estrito de 60 minutos (J5 e spec 2.6)
        if (trava.AdquiridaEm.AddMinutes(60) <= agora)
        {
            _logger.LogInformation("[SuporteRemoto] Sessão {SessionId} atingiu limite de 60 minutos. Encerrando.", sessionId);
            await EncerrarSessaoInternoAsync(trava, "Limite de 60 minutos atingido", cancellationToken);
            return false;
        }

        // 2. Verifica se o técnico ainda está ativo
        var tecnico = await _dbContext.Tecnicos
            .FirstOrDefaultAsync(t => t.UsuarioDominio == trava.TecnicoUsername, cancellationToken);

        if (tecnico == null || !tecnico.Ativo)
        {
            _logger.LogWarning("[SuporteRemoto] Técnico {Tecnico} foi desativado durante a sessão {SessionId}. Encerrando.",
                trava.TecnicoUsername, sessionId);
            await EncerrarSessaoInternoAsync(trava, "Técnico desativado no sistema", cancellationToken);
            return false;
        }

        // 3. Renova a trava (+90s de validade)
        trava.UltimoHeartbeatEm = agora;
        trava.ExpiraEm = agora.AddSeconds(90);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task EncerrarSessaoAsync(string sessionId, string motivo, CancellationToken cancellationToken = default)
    {
        var trava = await _dbContext.TravasSessao
            .Include(t => t.Maquina)
            .FirstOrDefaultAsync(t => t.IdentificadorSessao == sessionId, cancellationToken);

        if (trava != null)
        {
            await EncerrarSessaoInternoAsync(trava, motivo, cancellationToken);
        }
    }

    public async Task RegistrarRecusaAsync(string sessionId, string motivo, CancellationToken cancellationToken = default)
    {
        var trava = await _dbContext.TravasSessao
            .Include(t => t.Maquina)
            .FirstOrDefaultAsync(t => t.IdentificadorSessao == sessionId, cancellationToken);

        if (trava == null) return;

        // 1. Derruba ativamente e revoga direitos
        await DerrubarSessaoERevogarDireitosAsync(trava.Maquina?.NodeId, trava.TecnicoUsername, cancellationToken);

        // 2. Remove a trava
        _dbContext.TravasSessao.Remove(trava);

        // 3. Localiza e atualiza a Acao correspondente
        var acao = await _dbContext.Acoes
            .Where(a => a.MaquinaId == trava.MaquinaId && a.Tipo == TiposAcao.SuporteRemoto && a.ConcluidoEm == null)
            .OrderByDescending(a => a.CriadoEm)
            .FirstOrDefaultAsync(cancellationToken);

        if (acao != null)
        {
            acao.Estado = EstadosAcao.Cancelada;
            acao.Resultado = "RECUSADO";
            acao.Detalhes = motivo;
            acao.ConcluidoEm = DateTimeOffset.UtcNow;
        }

        // 4. Registra auditoria com Sucesso = false (Recusa/Sem resposta) vinculada à ação
        var auditoria = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = trava.TecnicoUsername,
            Tipo = TiposAuditoria.AcessarTela,
            Alvo = trava.Maquina?.NumeroSerie,
            MaquinaId = trava.MaquinaId,
            AcaoId = acao?.Id,
            Sucesso = false,
            DetalhesJson = JsonSerializer.Serialize(new { evento = "recusa", motivo, sessionId })
        };
        _dbContext.Auditorias.Add(auditoria);

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[SuporteRemoto] Recusa registrada para sessão {SessionId}. Motivo: {Motivo}", sessionId, motivo);
    }

    public async Task EncerrarSessaoPorEventoUsuarioAsync(string nodeId, string motivo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;

        var maquina = await _dbContext.Maquinas
            .Include(m => m.TravaSessao)
            .FirstOrDefaultAsync(m => m.NodeId == nodeId, cancellationToken);

        if (maquina?.TravaSessao != null)
        {
            _logger.LogInformation("[SuporteRemoto] Encerrando sessão da máquina {NodeId} por evento de usuário local: {Motivo}",
                nodeId, motivo);
            await EncerrarSessaoInternoAsync(maquina.TravaSessao, motivo, cancellationToken);
        }
    }

    public async Task ReconciliarTravasOrfasAsync(CancellationToken cancellationToken = default)
    {
        var agora = DateTimeOffset.UtcNow;
        var travasExpiradas = await _dbContext.TravasSessao
            .Include(t => t.Maquina)
            .Where(t => t.ExpiraEm <= agora || t.AdquiridaEm.AddMinutes(60) <= agora)
            .ToListAsync(cancellationToken);

        foreach (var trava in travasExpiradas)
        {
            var motivo = trava.AdquiridaEm.AddMinutes(60) <= agora
                ? "Limite máximo de 60 minutos atingido"
                : "Trava expirada sem renovação (90s)";

            _logger.LogWarning("[SuporteRemoto] Reconciliando trava expirada {SessionId} da máquina {Serie}. Motivo: {Motivo}",
                trava.IdentificadorSessao, trava.Maquina?.NumeroSerie, motivo);

            await EncerrarSessaoInternoAsync(trava, motivo, cancellationToken);
        }
    }

    public async Task EncerrarSessoesTecnicoDesativadoAsync(string tecnicoUsername, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tecnicoUsername)) return;

        var travas = await _dbContext.TravasSessao
            .Include(t => t.Maquina)
            .Where(t => t.TecnicoUsername.ToLower() == tecnicoUsername.ToLower())
            .ToListAsync(cancellationToken);

        foreach (var trava in travas)
        {
            _logger.LogWarning("[SuporteRemoto] Encerrando sessão de suporte remoto {SessionId} do técnico desativado {Tecnico}",
                trava.IdentificadorSessao, tecnicoUsername);

            await EncerrarSessaoInternoAsync(trava, "Técnico desativado no sistema", cancellationToken);
        }
    }

    private async Task EncerrarSessaoInternoAsync(TravaSessao trava, string motivo, CancellationToken cancellationToken)
    {
        // 1. Derruba ativamente sessões e revoga direitos no MeshCentral
        await DerrubarSessaoERevogarDireitosAsync(trava.Maquina?.NodeId, trava.TecnicoUsername, cancellationToken);

        // 2. Libera a trava atômica no banco SOMENTE APÓS confirmar a derrubada e revogação
        _dbContext.TravasSessao.Remove(trava);

        // 3. Conclui ação correspondente
        var acao = await _dbContext.Acoes
            .Where(a => a.MaquinaId == trava.MaquinaId && a.Tipo == TiposAcao.SuporteRemoto && a.ConcluidoEm == null)
            .OrderByDescending(a => a.CriadoEm)
            .FirstOrDefaultAsync(cancellationToken);

        if (acao != null)
        {
            acao.Estado = EstadosAcao.Executada;
            acao.Resultado = "FINALIZADO";
            acao.Detalhes = motivo;
            acao.ConcluidoEm = DateTimeOffset.UtcNow;
        }

        // 4. Registra auditoria de término de sessão vinculada à ação
        var auditoria = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = string.IsNullOrWhiteSpace(trava.TecnicoUsername) ? "sistema" : trava.TecnicoUsername,
            Tipo = TiposAuditoria.AcessarTela,
            Alvo = trava.Maquina?.NumeroSerie,
            MaquinaId = trava.MaquinaId,
            AcaoId = acao?.Id,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new { evento = "fim", motivo, sessionId = trava.IdentificadorSessao })
        };
        _dbContext.Auditorias.Add(auditoria);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task DerrubarSessaoERevogarDireitosAsync(string? nodeId, string tecnicoUsername, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tecnicoUsername)) return;

        try
        {
            await _meshClient.DropUserSessionsAsync(tecnicoUsername, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SuporteRemoto] Erro ao fechar sessões no MeshCentral para {Tecnico}", tecnicoUsername);
        }

        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            try
            {
                await _meshClient.RevokeRemoteDesktopRightsAsync(nodeId, tecnicoUsername, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SuporteRemoto] Erro ao revogar direitos no nó {NodeId} para {Tecnico}", nodeId, tecnicoUsername);
            }
        }
    }

    private static string ObterMirrorUsername(string technicianUsername)
    {
        return technicianUsername.StartsWith("gtv_mirror_")
            ? technicianUsername
            : $"gtv_mirror_{technicianUsername}";
    }

    private static List<string> ObterUsuariosLogados(string? usuariosJson)
    {
        if (string.IsNullOrWhiteSpace(usuariosJson)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(usuariosJson);
            return list?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void OnRawMessageReceived(object? sender, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int msgid = 0;
            string? nodeId = null;

            if (root.TryGetProperty("msgid", out var msgidElem) && msgidElem.TryGetInt32(out var idVal))
            {
                msgid = idVal;
            }
            else if (root.TryGetProperty("event", out var evtElem) &&
                     evtElem.TryGetProperty("msgid", out var evtMsgid) &&
                     evtMsgid.TryGetInt32(out var idEvt))
            {
                msgid = idEvt;
                if (evtElem.TryGetProperty("nodeid", out var nidElem))
                {
                    nodeId = nidElem.GetString();
                }
            }

            if (root.TryGetProperty("nodeid", out var nodeElem))
            {
                nodeId = nodeElem.GetString();
            }

            if (msgid == 34) // Recusa ou timeout de 60s
            {
                if (!string.IsNullOrEmpty(nodeId))
                {
                    _ = Task.Run(async () =>
                    {
                        await ProcessarRecusaNodeAsync(nodeId, "Usuário local recusou o acesso ou não respondeu em 60 segundos.");
                    });
                }
            }
            else if (msgid is 29 or 53) // 29 = fechado pelo usuário, 53 = bloqueio de tela Win+L / logoff
            {
                if (!string.IsNullOrEmpty(nodeId))
                {
                    var motivo = msgid == 53
                        ? "Bloqueio de tela (Win+L) ou logoff no Windows."
                        : "Encerrado pelo usuário na barra de privacidade.";

                    _ = Task.Run(async () => await EncerrarSessaoPorEventoUsuarioAsync(nodeId, motivo));
                }
            }
        }
        catch
        {
            // Ignora mensagens que não sejam eventos estruturados de consentimento
        }
    }

    private async Task ProcessarRecusaNodeAsync(string nodeId, string motivo)
    {
        var maquina = await _dbContext.Maquinas
            .Include(m => m.TravaSessao)
            .FirstOrDefaultAsync(m => m.NodeId == nodeId);

        if (maquina?.TravaSessao != null)
        {
            await RegistrarRecusaAsync(maquina.TravaSessao.IdentificadorSessao, motivo);
        }
    }
}
