using System.Text.Json;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Services;

public class SuporteRemotoWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SuporteRemotoWorker> _logger;

    public SuporteRemotoWorker(
        IServiceProvider serviceProvider,
        ILogger<SuporteRemotoWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Reconciliação na subida do serviço (J5 e seção 2.6 da spec):
        // Qualquer trava deixada de execução anterior tem direito e sessão revogados na hora
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GtViewerDbContext>();
            var meshClient = scope.ServiceProvider.GetRequiredService<IMeshCentralClient>();

            var travasOrfas = await dbContext.TravasSessao
                .Include(t => t.Maquina)
                .ToListAsync(cancellationToken);

            foreach (var trava in travasOrfas)
            {
                _logger.LogWarning("[SuporteRemotoWorker] Revogando trava órfã detectada na inicialização do serviço: {SessionId} ({Tecnico})",
                    trava.IdentificadorSessao, trava.TecnicoUsername);

                try
                {
                    await meshClient.DropUserSessionsAsync(trava.TecnicoUsername, cancellationToken: cancellationToken);
                    if (!string.IsNullOrWhiteSpace(trava.Maquina?.NodeId))
                    {
                        await meshClient.RevokeRemoteDesktopRightsAsync(trava.Maquina.NodeId, trava.TecnicoUsername, cancellationToken: cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SuporteRemotoWorker] Erro ao revogar sessão órfã no MeshCentral para {Tecnico}", trava.TecnicoUsername);
                }

                dbContext.TravasSessao.Remove(trava);

                var auditoria = new Auditoria
                {
                    DataHora = DateTimeOffset.UtcNow,
                    Ator = "sistema",
                    Tipo = TiposAuditoria.RevogarDireitoOrfaoTela,
                    Alvo = trava.Maquina?.NumeroSerie,
                    MaquinaId = trava.MaquinaId,
                    Sucesso = true,
                    DetalhesJson = JsonSerializer.Serialize(new
                    {
                        evento = "subida_gt_viewer",
                        sessionId = trava.IdentificadorSessao,
                        tecnico = trava.TecnicoUsername
                    })
                };
                dbContext.Auditorias.Add(auditoria);
            }

            if (travasOrfas.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SuporteRemotoWorker] Falha na reconciliação de travas durante o StartAsync.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var suporteService = scope.ServiceProvider.GetRequiredService<ISuporteRemotoService>();
                await suporteService.ReconciliarTravasOrfasAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SuporteRemotoWorker] Erro no ciclo de reconciliação de travas de suporte remoto.");
            }
        }
    }
}
