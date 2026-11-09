using System.Text.Json;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Services;

public class ReiniciarService : IReiniciarService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly IMeshCentralClient _meshClient;
    private readonly IAgentScriptProvider _scriptProvider;
    private readonly ILogger<ReiniciarService> _logger;

    public ReiniciarService(
        GtViewerDbContext dbContext,
        IMeshCentralClient meshClient,
        IAgentScriptProvider scriptProvider,
        ILogger<ReiniciarService> logger)
    {
        _dbContext = dbContext;
        _meshClient = meshClient;
        _scriptProvider = scriptProvider;
        _logger = logger;
    }

    public async Task<Acao> SolicitarReinicioAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default)
    {
        // 1. Validação de permissão do técnico
        var tecnico = await _dbContext.Tecnicos
            .FirstOrDefaultAsync(t => t.UsuarioDominio == tecnicoUsername, cancellationToken);

        if (tecnico != null)
        {
            if (!tecnico.Ativo)
            {
                throw new UnauthorizedAccessException("Técnico inativo no sistema.");
            }
            if (!tecnico.PossuiPermissao(Permissoes.Reiniciar))
            {
                throw new UnauthorizedAccessException("Técnico não possui permissão para reiniciar máquinas.");
            }
        }
        else if (tecnicoUsername != "sistema")
        {
            throw new UnauthorizedAccessException("Técnico não encontrado ou sem permissão para reiniciar máquinas.");
        }

        // 2. Validação da conexão com o MeshCentral (J7 e Convenções)
        if (!_meshClient.IsConnected)
        {
            throw new InvalidOperationException("Sem conexão com o servidor de máquinas (MeshCentral) — reinício indisponível.");
        }

        // 3. Validação do estado da máquina (J7: máquina offline não aceita reinício nem fica pendente)
        var maquina = await _dbContext.Maquinas
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null)
        {
            throw new ArgumentException($"Máquina com ID {maquinaId} não encontrada.", nameof(maquinaId));
        }

        if (maquina.ConexaoEstado != "online")
        {
            throw new InvalidOperationException("A máquina não está online. O reinício não fica pendente para equipamentos offline.");
        }

        if (string.IsNullOrWhiteSpace(maquina.NodeId))
        {
            throw new InvalidOperationException("A máquina não possui identificador de conexão (NodeId).");
        }

        // 4. Invariante 5: Gravação de Ação e Auditoria na MESMA transação antes de qualquer efeito externo
        var parametrosJson = JsonSerializer.Serialize(new
        {
            delay = 120,
            aviso = "A TI vai reiniciar seu computador em 2 minutos. Salve seu trabalho."
        });

        var acao = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = TiposAcao.Reiniciar,
            Prioridade = 0, // Ação imediata (não concorre na fila de tarefas em lote)
            Estado = EstadosAcao.Enviada,
            Ator = tecnicoUsername,
            ParametrosJson = parametrosJson,
            CriadoEm = DateTimeOffset.UtcNow
        };

        var auditoria = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = tecnicoUsername,
            Tipo = TiposAuditoria.Reiniciar,
            Alvo = maquina.NumeroSerie,
            MaquinaId = maquina.Id,
            Sucesso = true,
            DetalhesJson = parametrosJson
        };

        await using (var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            _dbContext.Acoes.Add(acao);
            await _dbContext.SaveChangesAsync(cancellationToken);

            auditoria.AcaoId = acao.Id;
            _dbContext.Auditorias.Add(auditoria);

            if (tecnico != null && tecnico.IsContaEmergencia)
            {
                var alerta = new AlertaEmergencia
                {
                    DataHora = DateTimeOffset.UtcNow,
                    Ator = tecnicoUsername,
                    Mensagem = $"Ação de reinício executada na máquina {maquina.NumeroSerie} pela conta de emergência.",
                    Reconhecido = false
                };
                _dbContext.AlertasEmergencia.Add(alerta);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // 5. Envio do script fixo versionado via MeshCentral
        try
        {
            var script = await _scriptProvider.ObterScriptAsync("ReiniciarMaquina.ps1", cancellationToken);
            var runResult = await _meshClient.RunFixedScriptAsync(
                maquina.NodeId,
                script,
                TimeSpan.FromSeconds(60),
                cancellationToken);

            if (runResult.ExitCode == 0 || runResult.GtvResult is "REINICIANDO" or "OK")
            {
                acao.Estado = EstadosAcao.Executada;
                acao.Resultado = runResult.GtvResult ?? "REINICIANDO";
                acao.CodigoSaida = runResult.ExitCode;
                acao.ConcluidoEm = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("[ReiniciarService] Reinício disparado com sucesso para {Serie} por {Tecnico}.",
                    maquina.NumeroSerie, tecnicoUsername);

                return acao;
            }
            else
            {
                acao.Estado = EstadosAcao.Falhou;
                acao.Resultado = runResult.GtvResult ?? "FALHA";
                acao.CodigoSaida = runResult.ExitCode;
                acao.Detalhes = runResult.RawOutput ?? "Comando de reinício retornou código diferente de zero.";
                acao.ConcluidoEm = DateTimeOffset.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogWarning("[ReiniciarService] Falha ao executar reinício na máquina {Serie}: {Erro}",
                    maquina.NumeroSerie, acao.Detalhes);

                throw new InvalidOperationException($"Falha ao reiniciar o computador: {acao.Detalhes}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "[ReiniciarService] Erro ao comunicar com MeshCentral ao reiniciar {Serie}", maquina.NumeroSerie);

            acao.Estado = EstadosAcao.Falhou;
            acao.Resultado = "FALHA";
            acao.Detalhes = ex.Message;
            acao.ConcluidoEm = DateTimeOffset.UtcNow;

            try
            {
                await _dbContext.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "[ReiniciarService] Erro ao persistir falha da ação {AcaoId}", acao.Id);
            }

            throw new InvalidOperationException($"Erro de comunicação ao enviar comando de reinício: {ex.Message}", ex);
        }
    }
}
