using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GtViewer.Web.Services;

public class FilaAcoesBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMeshCentralClient _meshClient;
    private readonly ILogger<FilaAcoesBackgroundService> _logger;

    public FilaAcoesBackgroundService(
        IServiceProvider serviceProvider,
        IMeshCentralClient meshClient,
        ILogger<FilaAcoesBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _meshClient = meshClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[FilaAcoesBackgroundService] Iniciando serviço de fila de ações em segundo plano...");

        // 1. Reconciliação de ações órfãs na subida do sistema (spec 2.4)
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var filaService = scope.ServiceProvider.GetRequiredService<IFilaAcoesService>();
            await filaService.ReconciliarAcoesOrfasNaSubidaAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "[FilaAcoesBackgroundService] Erro ao reconciliar ações órfãs na subida.");
        }

        // 2. Inscrição nos eventos de conexão/desconexão de nós do MeshCentral
        _meshClient.NodeConnected += OnNodeConnected;
        _meshClient.NodeDisconnected += OnNodeDisconnected;

        var lastFiveMinuteCycle = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_meshClient.IsConnected) continue;

                using var scope = _serviceProvider.CreateScope();
                var filaService = scope.ServiceProvider.GetRequiredService<IFilaAcoesService>();

                // Ciclo curto (5s): timeouts, retentativas OCUPADO e despacho
                try
                {
                    await filaService.ProcessarCicloFilaAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "[FilaAcoesBackgroundService] Erro no ciclo de processamento da fila.");
                }

                // Ciclo de 5 minutos: reconciliação para todas as máquinas online
                if (DateTimeOffset.UtcNow - lastFiveMinuteCycle >= TimeSpan.FromMinutes(5))
                {
                    lastFiveMinuteCycle = DateTimeOffset.UtcNow;
                    try
                    {
                        await filaService.ReconciliarMaquinasOnlineAsync(stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "[FilaAcoesBackgroundService] Erro no ciclo de reconciliação periódica de 5 min.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento gracioso
        }
        finally
        {
            _meshClient.NodeConnected -= OnNodeConnected;
            _meshClient.NodeDisconnected -= OnNodeDisconnected;
            _logger.LogInformation("[FilaAcoesBackgroundService] Serviço de fila de ações encerrado.");
        }
    }

    private void OnNodeConnected(object? sender, Models.MeshCentral.MeshNodeEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var filaService = scope.ServiceProvider.GetRequiredService<IFilaAcoesService>();
                await filaService.ProcessarNodeConnectedAsync(e.NodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FilaAcoesBackgroundService] Erro ao processar NodeConnected para nó {NodeId}.", e.NodeId);
            }
        });
    }

    private void OnNodeDisconnected(object? sender, Models.MeshCentral.MeshNodeEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var filaService = scope.ServiceProvider.GetRequiredService<IFilaAcoesService>();
                await filaService.TratarDesconexaoMaquinaAsync(e.NodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FilaAcoesBackgroundService] Erro ao processar NodeDisconnected para nó {NodeId}.", e.NodeId);
            }
        });
    }
}
