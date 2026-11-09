using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Models.MeshCentral;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GtViewer.Web.Services;

public class FilaAcoesService : IFilaAcoesService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly IMeshCentralClient _meshClient;
    private readonly IAgentScriptProvider _scriptProvider;
    private readonly IRescuePasswordService _rescuePasswordService;
    private readonly MeshCentralOptions _options;
    private readonly ILogger<FilaAcoesService> _logger;

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> MachineLocks = new();

    public FilaAcoesService(
        GtViewerDbContext dbContext,
        IMeshCentralClient meshClient,
        IAgentScriptProvider scriptProvider,
        IRescuePasswordService rescuePasswordService,
        IOptions<MeshCentralOptions> options,
        ILogger<FilaAcoesService> logger)
    {
        _dbContext = dbContext;
        _meshClient = meshClient;
        _scriptProvider = scriptProvider;
        _rescuePasswordService = rescuePasswordService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Acao> EnfileirarAcaoAsync(
        int maquinaId,
        string tipo,
        string ator,
        string? parametrosJson = null,
        int? prioridade = null,
        int? tarefaId = null,
        CancellationToken cancellationToken = default)
    {
        var maquina = await _dbContext.Maquinas
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);
        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        var prioridadeEfetiva = prioridade ?? PrioridadesAcao.ObterPrioridadePadrao(tipo);

        // Invariante 5: Gravação de Acao e Auditoria na mesma transação atômica
        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var acao = new Acao
        {
            MaquinaId = maquinaId,
            Tipo = tipo,
            Prioridade = prioridadeEfetiva,
            Estado = EstadosAcao.Pendente,
            Ator = string.IsNullOrWhiteSpace(ator) ? "sistema" : ator,
            ParametrosJson = parametrosJson,
            TarefaId = tarefaId,
            Tentativas = 0,
            CriadoEm = DateTimeOffset.UtcNow
        };

        _dbContext.Acoes.Add(acao);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var auditoria = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = acao.Ator,
            Tipo = MapearTipoAuditoria(tipo),
            Alvo = maquina.NumeroSerie,
            MaquinaId = maquina.Id,
            AcaoId = acao.Id,
            Sucesso = true,
            DetalhesJson = parametrosJson
        };

        _dbContext.Auditorias.Add(auditoria);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("[FilaAcoes] Ação {AcaoId} ({Tipo}, P{Prioridade}) enfileirada para máquina {Serie}",
            acao.Id, acao.Tipo, acao.Prioridade, maquina.NumeroSerie);

        return acao;
    }

    public async Task<bool> CancelarAcaoAsync(
        int acaoId,
        string ator,
        string? motivo = null,
        CancellationToken cancellationToken = default)
    {
        var acao = await _dbContext.Acoes
            .Include(a => a.Maquina)
            .FirstOrDefaultAsync(a => a.Id == acaoId, cancellationToken);

        if (acao == null) return false;

        if (acao.Estado == EstadosAcao.Executada || acao.Estado == EstadosAcao.Falhou || acao.Estado == EstadosAcao.Cancelada)
        {
            return false;
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        acao.Estado = EstadosAcao.Cancelada;
        acao.ConcluidoEm = DateTimeOffset.UtcNow;
        acao.Detalhes = motivo ?? "Ação cancelada.";

        var auditoria = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = string.IsNullOrWhiteSpace(ator) ? "sistema" : ator,
            Tipo = TiposAuditoria.CancelarPendencia,
            Alvo = acao.Maquina?.NumeroSerie,
            MaquinaId = acao.MaquinaId,
            AcaoId = acao.Id,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new { acao.Tipo, acao.Id, motivo })
        };

        _dbContext.Auditorias.Add(auditoria);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("[FilaAcoes] Ação {AcaoId} cancelada por {Ator}.", acaoId, ator);
        return true;
    }

    public async Task ProcessarMaquinaAsync(
        int maquinaId,
        CancellationToken cancellationToken = default)
    {
        var machineLock = MachineLocks.GetOrAdd(maquinaId, _ => new SemaphoreSlim(1, 1));
        await machineLock.WaitAsync(cancellationToken);

        try
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Em PostgreSQL real, bloqueia a linha da máquina com FOR UPDATE SKIP LOCKED
            if (_dbContext.Database.IsNpgsql())
            {
                var locked = await _dbContext.Maquinas
                    .FromSqlRaw("SELECT * FROM \"Maquinas\" WHERE \"Id\" = {0} FOR UPDATE SKIP LOCKED", maquinaId)
                    .FirstOrDefaultAsync(cancellationToken);

                if (locked == null)
                {
                    // Outro processo concorrente já está processando esta máquina
                    return;
                }
            }

            var maquina = await _dbContext.Maquinas
                .Include(m => m.Acoes)
                .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

            if (maquina == null) return;

            // 1. Verificar se existe ação ativa na máquina: enviada ou resultado_desconhecido
            var acaoAtiva = maquina.Acoes
                .FirstOrDefault(a => a.Estado == EstadosAcao.Enviada || a.Estado == EstadosAcao.ResultadoDesconhecido);

            if (acaoAtiva != null)
            {
                if (acaoAtiva.Estado == EstadosAcao.Enviada)
                {
                    var timeout = ObterTimeoutPorTipo(acaoAtiva.Tipo);
                    var decorrido = DateTimeOffset.UtcNow - (acaoAtiva.EnviadoEm ?? DateTimeOffset.UtcNow);
                    var offline = !IsMaquinaOnline(maquina);

                    if (decorrido > timeout || offline)
                    {
                        acaoAtiva.Estado = EstadosAcao.ResultadoDesconhecido;
                        acaoAtiva.Detalhes = offline
                            ? "Máquina desconectada durante execução da ação."
                            : $"Timeout de {timeout.TotalMinutes} min excedido sem confirmação.";

                        await RegistrarAuditoriaTransicaoAsync(acaoAtiva, maquina, false, cancellationToken);
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }
                    else
                    {
                        // Ação ainda está em andamento no agente dentro do timeout: nenhuma outra ação pode ser enviada
                        await tx.CommitAsync(cancellationToken);
                        return;
                    }
                }

                if (acaoAtiva.Estado == EstadosAcao.ResultadoDesconhecido)
                {
                    if (IsMaquinaOnline(maquina))
                    {
                        // Máquina online -> Reconciliar resultado_desconhecido
                        await ReconciliarAcaoAsync(acaoAtiva, maquina, cancellationToken);
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }

                    // Se a ação ainda continua ativa (não atingiu executada, falhou ou cancelada), bloqueia envio de pendentes
                    if (acaoAtiva.Estado == EstadosAcao.Enviada || acaoAtiva.Estado == EstadosAcao.ResultadoDesconhecido)
                    {
                        await tx.CommitAsync(cancellationToken);
                        return;
                    }
                }
            }

            // Se a máquina estiver online e a senha de resgate tiver sido revelada,
            // garante que haja uma troca de senha de resgate (P1) antes de qualquer outra ação (spec J6)
            if (IsMaquinaOnline(maquina) && maquina.SenhaResgateRevelada)
            {
                var temAcaoResgateAtivaOuPendente = maquina.Acoes.Any(a =>
                    (a.Tipo is TiposAcao.TrocarResgate or TiposAcao.RedefinirResgate or TiposAcao.GarantirResgate) &&
                    (a.Estado is EstadosAcao.Pendente or EstadosAcao.Enviada or EstadosAcao.ResultadoDesconhecido));

                if (!temAcaoResgateAtivaOuPendente)
                {
                    if (string.IsNullOrEmpty(maquina.SenhaResgateCandidataCifrada))
                    {
                        var novaSenha = _rescuePasswordService.GeneratePassword();
                        maquina.SenhaResgateCandidataCifrada = _rescuePasswordService.EncryptPassword(novaSenha);
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }

                    var acaoTroca = new Acao
                    {
                        MaquinaId = maquina.Id,
                        Tipo = TiposAcao.TrocarResgate,
                        Prioridade = PrioridadesAcao.Resgate,
                        Estado = EstadosAcao.Pendente,
                        Ator = "sistema",
                        ParametrosJson = JsonSerializer.Serialize(new { motivo = "Troca obrigatória após revelação da senha de resgate" }),
                        CriadoEm = DateTimeOffset.UtcNow
                    };
                    _dbContext.Acoes.Add(acaoTroca);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var auditTroca = new Auditoria
                    {
                        DataHora = DateTimeOffset.UtcNow,
                        Ator = "sistema",
                        Tipo = TiposAuditoria.TrocarSenhaResgate,
                        Alvo = maquina.NumeroSerie,
                        MaquinaId = maquina.Id,
                        AcaoId = acaoTroca.Id,
                        Sucesso = true,
                        DetalhesJson = acaoTroca.ParametrosJson
                    };
                    _dbContext.Auditorias.Add(auditTroca);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            // 2. Não há ação ativa na máquina -> Buscar próxima ação pendente por prioridade e criação
            var proximaAcao = maquina.Acoes
                .Where(a => a.Estado == EstadosAcao.Pendente)
                .OrderBy(a => a.Prioridade)
                .ThenBy(a => a.CriadoEm)
                .FirstOrDefault();

            if (proximaAcao == null)
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }

            // Tratamento de "already busy" / OCUPADO: retentativa após 30s ou falha após 1h
            if (proximaAcao.Resultado == "OCUPADO")
            {
                var (inicioOcupado, proximoOcupado) = LerOcupadoInfo(proximaAcao.Detalhes);
                var agora = DateTimeOffset.UtcNow;

                if (inicioOcupado.HasValue && (agora - inicioOcupado.Value) > TimeSpan.FromHours(1))
                {
                    // Limite de 1 hora excedido em OCUPADO -> Falha
                    proximaAcao.Estado = EstadosAcao.Falhou;
                    proximaAcao.Detalhes = "Limite de 1h em estado ocupado excedido.";
                    proximaAcao.ConcluidoEm = agora;

                    await RegistrarAuditoriaTransicaoAsync(proximaAcao, maquina, false, cancellationToken);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return;
                }

                if (proximoOcupado.HasValue && agora < proximoOcupado.Value)
                {
                    // Ainda aguardando o ciclo de 30s de retentativa
                    await tx.CommitAsync(cancellationToken);
                    return;
                }
            }

            // Se a máquina estiver offline, a ação fica pendente até ligar
            if (!IsMaquinaOnline(maquina))
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }

            // 3. Despachar a ação
            await tx.CommitAsync(cancellationToken);
            await DespacharAcaoAsync(proximaAcao.Id, cancellationToken);
        }
        finally
        {
            machineLock.Release();
        }
    }

    public async Task ProcessarNodeConnectedAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var maquina = await _dbContext.Maquinas
            .FirstOrDefaultAsync(m => m.NodeId == nodeId, cancellationToken);

        if (maquina == null) return;

        maquina.ConexaoEstado = "online";
        maquina.UltimoContatoEm = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await ProcessarMaquinaAsync(maquina.Id, cancellationToken);
    }

    public async Task TratarDesconexaoMaquinaAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var maquina = await _dbContext.Maquinas
            .Include(m => m.Acoes)
            .FirstOrDefaultAsync(m => m.NodeId == nodeId, cancellationToken);

        if (maquina == null) return;

        maquina.ConexaoEstado = "offline";

        var enviadas = maquina.Acoes
            .Where(a => a.Estado == EstadosAcao.Enviada)
            .ToList();

        if (enviadas.Count > 0)
        {
            foreach (var acao in enviadas)
            {
                acao.Estado = EstadosAcao.ResultadoDesconhecido;
                acao.Detalhes = "Máquina desconectada durante execução da ação.";
                await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReconciliarAcoesOrfasNaSubidaAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[FilaAcoes] Verificando ações órfãs na subida do sistema...");

        var acoesOrfas = await _dbContext.Acoes
            .Include(a => a.Maquina)
            .Where(a => a.Estado == EstadosAcao.Enviada)
            .ToListAsync(cancellationToken);

        if (acoesOrfas.Count > 0)
        {
            foreach (var acao in acoesOrfas)
            {
                acao.Estado = EstadosAcao.ResultadoDesconhecido;
                acao.Detalhes = "Ação órfã identificada na inicialização do serviço.";
                await RegistrarAuditoriaTransicaoAsync(acao, acao.Maquina, false, cancellationToken);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("[FilaAcoes] {Count} ações órfãs transicionadas para resultado_desconhecido.", acoesOrfas.Count);
        }

        // Para máquinas online com ações em resultado_desconhecido, agenda reconciliação
        var maquinasParaReconciliar = acoesOrfas
            .Select(a => a.MaquinaId)
            .Distinct()
            .ToList();

        foreach (var mId in maquinasParaReconciliar)
        {
            var m = await _dbContext.Maquinas.FindAsync(new object[] { mId }, cancellationToken);
            if (m != null && IsMaquinaOnline(m))
            {
                _ = ProcessarMaquinaAsync(mId, cancellationToken);
            }
        }
    }

    public async Task ProcessarCicloFilaAsync(
        CancellationToken cancellationToken = default)
    {
        var agora = DateTimeOffset.UtcNow;

        // 1. Identificar timeouts de ações enviadas
        var enviadas = await _dbContext.Acoes
            .Include(a => a.Maquina)
            .Where(a => a.Estado == EstadosAcao.Enviada)
            .ToListAsync(cancellationToken);

        foreach (var acao in enviadas)
        {
            var timeout = ObterTimeoutPorTipo(acao.Tipo);
            var decorrido = agora - (acao.EnviadoEm ?? agora);
            var offline = acao.Maquina == null || !IsMaquinaOnline(acao.Maquina);

            if (decorrido > timeout || offline)
            {
                acao.Estado = EstadosAcao.ResultadoDesconhecido;
                acao.Detalhes = offline
                    ? "Máquina desconectada durante execução da ação."
                    : $"Timeout de {timeout.TotalMinutes} min excedido sem confirmação.";

                await RegistrarAuditoriaTransicaoAsync(acao, acao.Maquina, false, cancellationToken);
            }
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 2. Buscar máquinas que possuem ações pendentes ou em resultado_desconhecido
        var maquinasIds = await _dbContext.Maquinas
            .Where(m => m.Acoes.Any(a => a.Estado == EstadosAcao.Pendente || a.Estado == EstadosAcao.ResultadoDesconhecido))
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        foreach (var maquinaId in maquinasIds)
        {
            try
            {
                await ProcessarMaquinaAsync(maquinaId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FilaAcoes] Erro ao processar fila da máquina {MaquinaId}.", maquinaId);
            }
        }
    }

    public async Task ReconciliarMaquinasOnlineAsync(
        CancellationToken cancellationToken = default)
    {
        var maquinasComDesconhecido = await _dbContext.Maquinas
            .Where(m => m.Acoes.Any(a => a.Estado == EstadosAcao.ResultadoDesconhecido))
            .ToListAsync(cancellationToken);

        foreach (var maquina in maquinasComDesconhecido)
        {
            if (IsMaquinaOnline(maquina))
            {
                try
                {
                    await ProcessarMaquinaAsync(maquina.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[FilaAcoes] Erro na reconciliação periódica da máquina {Serie}.", maquina.NumeroSerie);
                }
            }
        }
    }

    public async Task<Tarefa> CriarTarefaGrupoAsync(
        int grupoId,
        string tipo,
        string ator,
        int? appCatalogoId = null,
        string? parametrosJson = null,
        CancellationToken cancellationToken = default)
    {
        var maquinas = await _dbContext.Maquinas
            .Where(m => m.GrupoId == grupoId && !m.EmAprovacao)
            .ToListAsync(cancellationToken);

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var tarefa = new Tarefa
        {
            GrupoId = grupoId,
            Tipo = tipo,
            Ator = string.IsNullOrWhiteSpace(ator) ? "sistema" : ator,
            AppCatalogoId = appCatalogoId,
            CriadoEm = DateTimeOffset.UtcNow
        };

        _dbContext.Tarefas.Add(tarefa);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var m in maquinas)
        {
            var acao = new Acao
            {
                MaquinaId = m.Id,
                TarefaId = tarefa.Id,
                Tipo = tipo,
                Prioridade = PrioridadesAcao.Aplicativos,
                Estado = EstadosAcao.Pendente,
                Ator = tarefa.Ator,
                ParametrosJson = parametrosJson,
                CriadoEm = DateTimeOffset.UtcNow
            };
            _dbContext.Acoes.Add(acao);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var auditoria = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = tarefa.Ator,
            Tipo = tipo == TiposAcao.Instalar ? TiposAuditoria.Instalar : TiposAuditoria.Remover,
            Alvo = $"Grupo ID {grupoId}",
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new { tarefa.Id, grupoId, TotalMaquinas = maquinas.Count })
        };

        _dbContext.Auditorias.Add(auditoria);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("[FilaAcoes] Tarefa de grupo {TarefaId} criada com {Count} ações vinculadas.", tarefa.Id, maquinas.Count);
        return tarefa;
    }

    private async Task DespacharAcaoAsync(int acaoId, CancellationToken cancellationToken)
    {
        var acao = await _dbContext.Acoes
            .Include(a => a.Maquina)
            .FirstOrDefaultAsync(a => a.Id == acaoId, cancellationToken);

        if (acao == null || acao.Estado != EstadosAcao.Pendente) return;

        var maquina = acao.Maquina;
        if (maquina == null || string.IsNullOrEmpty(maquina.NodeId) || !IsMaquinaOnline(maquina)) return;

        // Invariante 5: Gravação de estado 'enviada' e Auditoria ANTES de qualquer efeito externo.
        // Se a transação falhar, nada é enviado ao MeshCentral.
        await using (var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            acao.Estado = EstadosAcao.Enviada;
            acao.EnviadoEm = DateTimeOffset.UtcNow;

            var auditEnvio = new Auditoria
            {
                DataHora = DateTimeOffset.UtcNow,
                Ator = acao.Ator,
                Tipo = MapearTipoAuditoria(acao.Tipo),
                Alvo = maquina.NumeroSerie,
                MaquinaId = maquina.Id,
                AcaoId = acao.Id,
                Sucesso = true,
                DetalhesJson = JsonSerializer.Serialize(new { evento = "despacho", acao.Tipo, acao.Tentativas })
            };

            _dbContext.Auditorias.Add(auditEnvio);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // Montagem do script e parâmetros específicos do tipo
        var script = await MontarScriptAcaoAsync(acao, maquina, cancellationToken);
        var timeout = ObterTimeoutPorTipo(acao.Tipo);

        try
        {
            var resultado = await _meshClient.RunFixedScriptAsync(maquina.NodeId, script, timeout, cancellationToken);
            await ProcessarResultadoExecucaoAsync(acao.Id, resultado, cancellationToken);
        }
        catch (Exception ex) when (ex is MeshCentralDisconnectedException or TimeoutException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[FilaAcoes] Falha/timeout ao executar script da ação {AcaoId} no nó {NodeId}.", acao.Id, maquina.NodeId);
            await MarcarResultadoDesconhecidoAsync(acao.Id, "Interrupção de comunicação ou timeout sem retorno do agente.", cancellationToken);
        }
    }

    private async Task ProcessarResultadoExecucaoAsync(int acaoId, RunCommandResult cmd, CancellationToken cancellationToken)
    {
        var acao = await _dbContext.Acoes
            .Include(a => a.Maquina)
            .Include(a => a.Tarefa)
            .FirstOrDefaultAsync(a => a.Id == acaoId, cancellationToken);

        if (acao == null) return;
        var maquina = acao.Maquina;

        var isOcupado = string.Equals(cmd.GtvResult, "OCUPADO", StringComparison.OrdinalIgnoreCase)
                        || cmd.RawOutput.Contains("already busy", StringComparison.OrdinalIgnoreCase);

        if (isOcupado)
        {
            // Resposta OCUPADO: não conta tentativa, agenda retentativa em 30s por até 1h
            var (inicioOcupado, _) = LerOcupadoInfo(acao.Detalhes);
            var agora = DateTimeOffset.UtcNow;
            var novoInicio = inicioOcupado ?? agora;

            if ((agora - novoInicio) > TimeSpan.FromHours(1))
            {
                acao.Estado = EstadosAcao.Falhou;
                acao.Resultado = "OCUPADO";
                acao.CodigoSaida = cmd.ExitCode;
                acao.Detalhes = "Limite de 1h em estado ocupado excedido.";
                acao.ConcluidoEm = agora;
                await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
            }
            else
            {
                acao.Estado = EstadosAcao.Pendente;
                acao.Resultado = "OCUPADO";
                acao.Detalhes = FormatarOcupadoInfo(novoInicio, agora.AddSeconds(30));
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (cmd.ExitCode == 0)
        {
            // Sucesso estrito
            acao.Estado = EstadosAcao.Executada;
            acao.Resultado = cmd.GtvResult;
            acao.CodigoSaida = 0;
            acao.ConcluidoEm = DateTimeOffset.UtcNow;

            AplicarEfeitosSucesso(acao, maquina);
            await RegistrarAuditoriaTransicaoAsync(acao, maquina, true, cancellationToken);

            if (acao.Tarefa != null)
            {
                await AtualizarProgressoTarefaAsync(acao.Tarefa.Id, cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // Erro na execução: conta tentativa (até 3)
            acao.Tentativas++;
            acao.Resultado = cmd.GtvResult;
            acao.CodigoSaida = cmd.ExitCode;

            if (acao.Tentativas >= 3)
            {
                acao.Estado = EstadosAcao.Falhou;
                acao.ConcluidoEm = DateTimeOffset.UtcNow;

                await TratarFalhaAcaoAsync(acao, maquina, cancellationToken);
                await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
            }
            else
            {
                acao.Estado = EstadosAcao.ResultadoDesconhecido;
                acao.Detalhes = $"Tentativa {acao.Tentativas}/3 falhou com código {cmd.ExitCode}. Aguardando reconciliação.";
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ReconciliarAcaoAsync(Acao acao, Maquina maquina, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[FilaAcoes] Reconciliando ação {AcaoId} ({Tipo}) para máquina {Serie}...",
            acao.Id, acao.Tipo, maquina.NumeroSerie);

        // Caso 1: Senha de resgate (script idempotente)
        if (acao.Tipo is TiposAcao.GarantirResgate or TiposAcao.TrocarResgate or TiposAcao.RedefinirResgate)
        {
            acao.Tentativas++;
            var script = await MontarScriptAcaoAsync(acao, maquina, cancellationToken);
            var timeout = ObterTimeoutPorTipo(acao.Tipo);

            try
            {
                var cmd = await _meshClient.RunFixedScriptAsync(maquina.NodeId!, script, timeout, cancellationToken);
                if (cmd.ExitCode == 0)
                {
                    acao.Estado = EstadosAcao.Executada;
                    acao.Resultado = cmd.GtvResult;
                    acao.CodigoSaida = 0;
                    acao.ConcluidoEm = DateTimeOffset.UtcNow;

                    AplicarEfeitosSucesso(acao, maquina);
                    await RegistrarAuditoriaTransicaoAsync(acao, maquina, true, cancellationToken);
                }
                else
                {
                    acao.Resultado = cmd.GtvResult;
                    acao.CodigoSaida = cmd.ExitCode;

                    if (acao.Tentativas >= 3)
                    {
                        acao.Estado = EstadosAcao.Falhou;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        await TratarFalhaAcaoAsync(acao, maquina, cancellationToken);
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FilaAcoes] Falha ao reconciliar senha de resgate na ação {AcaoId}.", acao.Id);
                if (acao.Tentativas >= 3)
                {
                    acao.Estado = EstadosAcao.Falhou;
                    acao.ConcluidoEm = DateTimeOffset.UtcNow;
                    await TratarFalhaAcaoAsync(acao, maquina, cancellationToken);
                    await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                }
            }
            return;
        }

        // Caso 2: Bloqueio ou Desbloqueio (roda script de verificação do estado real)
        if (acao.Tipo is TiposAcao.Bloquear or TiposAcao.Desbloquear)
        {
            var scriptVerif = await _scriptProvider.ObterScriptAsync("VerificarBloqueio.ps1", cancellationToken);
            try
            {
                var check = await _meshClient.RunFixedScriptAsync(maquina.NodeId!, scriptVerif, TimeSpan.FromMinutes(2), cancellationToken);

                // Auditoria do evento de verificação/reaplicação
                var auditVerif = new Auditoria
                {
                    DataHora = DateTimeOffset.UtcNow,
                    Ator = "sistema",
                    Tipo = TiposAuditoria.VerificarReaplicarBloqueio,
                    Alvo = maquina.NumeroSerie,
                    MaquinaId = maquina.Id,
                    AcaoId = acao.Id,
                    Sucesso = check.ExitCode == 0,
                    DetalhesJson = JsonSerializer.Serialize(new { check.GtvResult, check.ExitCode })
                };
                _dbContext.Auditorias.Add(auditVerif);

                if (acao.Tipo == TiposAcao.Bloquear)
                {
                    if (check.ExitCode == 0 && string.Equals(check.GtvResult, "BLOQUEADA", StringComparison.OrdinalIgnoreCase))
                    {
                        // Estado real já é o desejado (bloqueada)
                        acao.Estado = EstadosAcao.Executada;
                        acao.Resultado = "BLOQUEADA";
                        acao.CodigoSaida = 0;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        maquina.BloqueioEstado = "bloqueada";
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, true, cancellationToken);
                        return;
                    }

                    // Estado real não bate: reenvia BloquearMaquina.ps1 contando tentativa
                    acao.Tentativas++;
                    if (acao.Tentativas >= 3)
                    {
                        acao.Estado = EstadosAcao.Falhou;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        maquina.BloqueioEstado = "falha";
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                        return;
                    }

                    var scriptBlock = await MontarScriptAcaoAsync(acao, maquina, cancellationToken);
                    var cmdReenvio = await _meshClient.RunFixedScriptAsync(maquina.NodeId!, scriptBlock, TimeSpan.FromMinutes(2), cancellationToken);

                    if (cmdReenvio.ExitCode == 0 && string.Equals(cmdReenvio.GtvResult, "BLOQUEADA", StringComparison.OrdinalIgnoreCase))
                    {
                        acao.Estado = EstadosAcao.Executada;
                        acao.Resultado = "BLOQUEADA";
                        acao.CodigoSaida = 0;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        maquina.BloqueioEstado = "bloqueada";
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, true, cancellationToken);
                    }
                    else if (acao.Tentativas >= 3)
                    {
                        acao.Estado = EstadosAcao.Falhou;
                        acao.Resultado = cmdReenvio.GtvResult;
                        acao.CodigoSaida = cmdReenvio.ExitCode;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        maquina.BloqueioEstado = "falha";
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                    }
                }
                else if (acao.Tipo == TiposAcao.Desbloquear)
                {
                    if (check.ExitCode == 0 && string.Equals(check.GtvResult, "DESBLOQUEADA", StringComparison.OrdinalIgnoreCase))
                    {
                        // Estado real já é o desejado (desbloqueada)
                        acao.Estado = EstadosAcao.Executada;
                        acao.Resultado = "DESBLOQUEADA";
                        acao.CodigoSaida = 0;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        maquina.BloqueioEstado = "desbloqueada";
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, true, cancellationToken);
                        return;
                    }

                    // Estado real ainda é bloqueada: reenvia DesbloquearMaquina.ps1 contando tentativa
                    acao.Tentativas++;
                    if (acao.Tentativas >= 3)
                    {
                        acao.Estado = EstadosAcao.Falhou;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                        return;
                    }

                    var scriptUnlock = await MontarScriptAcaoAsync(acao, maquina, cancellationToken);
                    var cmdReenvio = await _meshClient.RunFixedScriptAsync(maquina.NodeId!, scriptUnlock, TimeSpan.FromMinutes(2), cancellationToken);

                    if (cmdReenvio.ExitCode == 0 && string.Equals(cmdReenvio.GtvResult, "DESBLOQUEADA", StringComparison.OrdinalIgnoreCase))
                    {
                        acao.Estado = EstadosAcao.Executada;
                        acao.Resultado = "DESBLOQUEADA";
                        acao.CodigoSaida = 0;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        maquina.BloqueioEstado = "desbloqueada";
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, true, cancellationToken);
                    }
                    else if (acao.Tentativas >= 3)
                    {
                        acao.Estado = EstadosAcao.Falhou;
                        acao.Resultado = cmdReenvio.GtvResult;
                        acao.CodigoSaida = cmdReenvio.ExitCode;
                        acao.ConcluidoEm = DateTimeOffset.UtcNow;
                        await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FilaAcoes] Falha ao executar script de verificação na máquina {Serie}.", maquina.NumeroSerie);
                if (acao.Tentativas >= 3)
                {
                    acao.Estado = EstadosAcao.Falhou;
                    acao.ConcluidoEm = DateTimeOffset.UtcNow;
                    await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                }
            }
            return;
        }

        // Caso 3: Instalação ou Remoção
        if (acao.Tipo is TiposAcao.Instalar or TiposAcao.Remover)
        {
            acao.Tentativas++;
            if (acao.Tentativas >= 3)
            {
                acao.Estado = EstadosAcao.Falhou;
                acao.ConcluidoEm = DateTimeOffset.UtcNow;
                await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                return;
            }

            var script = await MontarScriptAcaoAsync(acao, maquina, cancellationToken);
            try
            {
                var cmd = await _meshClient.RunFixedScriptAsync(maquina.NodeId!, script, ObterTimeoutPorTipo(acao.Tipo), cancellationToken);
                await ProcessarResultadoExecucaoAsync(acao.Id, cmd, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FilaAcoes] Falha ao reenviar instalação/remoção {AcaoId}.", acao.Id);
                if (acao.Tentativas >= 3)
                {
                    acao.Estado = EstadosAcao.Falhou;
                    acao.ConcluidoEm = DateTimeOffset.UtcNow;
                    await RegistrarAuditoriaTransicaoAsync(acao, maquina, false, cancellationToken);
                }
            }
        }
    }

    private async Task MarcarResultadoDesconhecidoAsync(int acaoId, string motivo, CancellationToken cancellationToken)
    {
        var acao = await _dbContext.Acoes
            .Include(a => a.Maquina)
            .FirstOrDefaultAsync(a => a.Id == acaoId, cancellationToken);

        if (acao == null) return;

        acao.Estado = EstadosAcao.ResultadoDesconhecido;
        acao.Detalhes = motivo;

        await RegistrarAuditoriaTransicaoAsync(acao, acao.Maquina, false, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void AplicarEfeitosSucesso(Acao acao, Maquina? maquina)
    {
        if (maquina == null) return;

        if (acao.Tipo == TiposAcao.Bloquear)
        {
            maquina.BloqueioEstado = "bloqueada";
        }
        else if (acao.Tipo == TiposAcao.Desbloquear)
        {
            maquina.BloqueioEstado = "desbloqueada";
        }
        else if (acao.Tipo is TiposAcao.GarantirResgate or TiposAcao.TrocarResgate or TiposAcao.RedefinirResgate)
        {
            if (!string.IsNullOrEmpty(maquina.SenhaResgateCandidataCifrada))
            {
                maquina.SenhaResgateAtualCifrada = maquina.SenhaResgateCandidataCifrada;
                maquina.SenhaResgateCandidataCifrada = null;
            }
            maquina.SenhaResgateRevelada = false;
            maquina.AtualizadoEm = DateTimeOffset.UtcNow;
        }
    }

    private async Task TratarFalhaAcaoAsync(Acao acao, Maquina? maquina, CancellationToken cancellationToken)
    {
        if (maquina == null) return;

        if (acao.Tipo == TiposAcao.Bloquear)
        {
            maquina.BloqueioEstado = "falha";
        }
        else if (acao.Tipo == TiposAcao.TrocarResgate)
        {
            _logger.LogWarning("[FilaAcoes] Troca de senha da máquina {Serie} falhou após 3 tentativas. Disparando redefinir conta de resgate.", maquina.NumeroSerie);
            maquina.SenhaResgateCandidataCifrada = null;
            var novaSenha = _rescuePasswordService.GeneratePassword();
            maquina.SenhaResgateCandidataCifrada = _rescuePasswordService.EncryptPassword(novaSenha);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var acaoRedefinir = new Acao
            {
                MaquinaId = maquina.Id,
                Tipo = TiposAcao.RedefinirResgate,
                Prioridade = PrioridadesAcao.Resgate,
                Estado = EstadosAcao.Pendente,
                Ator = "sistema",
                ParametrosJson = JsonSerializer.Serialize(new { motivo = "Disparo automático após falha em trocar senha de resgate" }),
                CriadoEm = DateTimeOffset.UtcNow
            };
            _dbContext.Acoes.Add(acaoRedefinir);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var auditRedefinir = new Auditoria
            {
                DataHora = DateTimeOffset.UtcNow,
                Ator = "sistema",
                Tipo = TiposAuditoria.RedefinirContaResgate,
                Alvo = maquina.NumeroSerie,
                MaquinaId = maquina.Id,
                AcaoId = acaoRedefinir.Id,
                Sucesso = true,
                DetalhesJson = acaoRedefinir.ParametrosJson
            };
            _dbContext.Auditorias.Add(auditRedefinir);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task AtualizarProgressoTarefaAsync(int tarefaId, CancellationToken cancellationToken)
    {
        var tarefa = await _dbContext.Tarefas
            .Include(t => t.Acoes)
            .FirstOrDefaultAsync(t => t.Id == tarefaId, cancellationToken);

        if (tarefa == null) return;

        var todasConcluidas = tarefa.Acoes.All(a =>
            a.Estado == EstadosAcao.Executada ||
            a.Estado == EstadosAcao.Falhou ||
            a.Estado == EstadosAcao.Cancelada);

        if (todasConcluidas && tarefa.ConcluidoEm == null)
        {
            tarefa.ConcluidoEm = DateTimeOffset.UtcNow;
        }
    }

    private async Task<string> MontarScriptAcaoAsync(Acao acao, Maquina maquina, CancellationToken cancellationToken)
    {
        switch (acao.Tipo)
        {
            case TiposAcao.GarantirResgate:
            case TiposAcao.TrocarResgate:
            case TiposAcao.RedefinirResgate:
            {
                var scriptBase = await _scriptProvider.ObterScriptAsync("GarantirContaResgate.ps1", cancellationToken);
                var token = Guid.NewGuid().ToString("N");
                acao.TokenAgenteHash = ComputeSha256(token);
                acao.TokenAgenteExpiraEm = DateTimeOffset.UtcNow.AddMinutes(5);

                var serverUrl = _options.ServerUrl ?? "https://localhost";
                var header = $"$PasswordToken = '{token}';\n$ServerUrl = '{serverUrl}';\n";
                if (maquina.SenhaResgateRevelada)
                {
                    header += "$Revealed = $true;\n";
                }
                return header + scriptBase;
            }

            case TiposAcao.Bloquear:
            {
                var scriptBase = await _scriptProvider.ObterScriptAsync("BloquearMaquina.ps1", cancellationToken);
                if (!string.IsNullOrWhiteSpace(acao.ParametrosJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(acao.ParametrosJson);
                        if (doc.RootElement.TryGetProperty("motivo", out var motivoElem) && !string.IsNullOrWhiteSpace(motivoElem.GetString()))
                        {
                            var motivo = motivoElem.GetString()!.Replace("'", "''");
                            return $"$ContactMessage = '{motivo}';\n" + scriptBase;
                        }
                    }
                    catch
                    {
                        // Se não for JSON válido de motivo, prossegue com script padrão
                    }
                }
                return scriptBase;
            }

            case TiposAcao.Desbloquear:
            {
                var scriptBase = await _scriptProvider.ObterScriptAsync("DesbloquearMaquina.ps1", cancellationToken);
                return scriptBase;
            }

            case TiposAcao.Instalar:
            {
                return await _scriptProvider.ObterScriptAsync("InstalarApp.ps1", cancellationToken);
            }

            case TiposAcao.Remover:
            {
                return await _scriptProvider.ObterScriptAsync("RemoverApp.ps1", cancellationToken);
            }

            default:
                return await _scriptProvider.ObterScriptAsync($"{acao.Tipo}.ps1", cancellationToken);
        }
    }

    private async Task RegistrarAuditoriaTransicaoAsync(Acao acao, Maquina? maquina, bool sucesso, CancellationToken cancellationToken)
    {
        var auditoria = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = acao.Ator,
            Tipo = MapearTipoAuditoria(acao.Tipo),
            Alvo = maquina?.NumeroSerie,
            MaquinaId = acao.MaquinaId,
            AcaoId = acao.Id,
            Sucesso = sucesso,
            DetalhesJson = JsonSerializer.Serialize(new
            {
                acao.Id,
                acao.Tipo,
                acao.Estado,
                acao.Resultado,
                acao.CodigoSaida,
                acao.Tentativas,
                acao.Detalhes
            })
        };

        _dbContext.Auditorias.Add(auditoria);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private bool IsMaquinaOnline(Maquina maquina)
    {
        if (string.IsNullOrEmpty(maquina.NodeId)) return false;

        if (_meshClient.CachedNodes.TryGetValue(maquina.NodeId, out var node))
        {
            var conn = (node["conn"]?.GetValue<int>() ?? 0) & 1;
            if (conn != 0) return true;
        }

        return string.Equals(maquina.ConexaoEstado, "online", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ObterTimeoutPorTipo(string tipo)
    {
        return tipo switch
        {
            TiposAcao.Instalar or TiposAcao.Remover => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromMinutes(2)
        };
    }

    private static string MapearTipoAuditoria(string tipoAcao)
    {
        return tipoAcao switch
        {
            TiposAcao.GarantirResgate => TiposAuditoria.GarantirContaResgate,
            TiposAcao.RedefinirResgate => TiposAuditoria.RedefinirContaResgate,
            TiposAcao.TrocarResgate => TiposAuditoria.TrocarSenhaResgate,
            TiposAcao.Bloquear => TiposAuditoria.Bloquear,
            TiposAcao.Desbloquear => TiposAuditoria.Desbloquear,
            TiposAcao.Instalar => TiposAuditoria.Instalar,
            TiposAcao.Remover => TiposAuditoria.Remover,
            _ => tipoAcao
        };
    }

    private static (DateTimeOffset? Inicio, DateTimeOffset? Proximo) LerOcupadoInfo(string? detalhes)
    {
        if (string.IsNullOrEmpty(detalhes)) return (null, null);

        DateTimeOffset? inicio = null;
        DateTimeOffset? proximo = null;

        var partes = detalhes.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var parte in partes)
        {
            if (parte.StartsWith("OCUPADO_INICIO:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(parte["OCUPADO_INICIO:".Length..], out var ticks))
                {
                    inicio = new DateTimeOffset(ticks, TimeSpan.Zero);
                }
            }
            else if (parte.StartsWith("OCUPADO_PROXIMO:", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(parte["OCUPADO_PROXIMO:".Length..], out var ticks))
                {
                    proximo = new DateTimeOffset(ticks, TimeSpan.Zero);
                }
            }
        }

        return (inicio, proximo);
    }

    private static string FormatarOcupadoInfo(DateTimeOffset inicio, DateTimeOffset proximo)
    {
        return $"OCUPADO_INICIO:{inicio.UtcTicks};OCUPADO_PROXIMO:{proximo.UtcTicks}";
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
