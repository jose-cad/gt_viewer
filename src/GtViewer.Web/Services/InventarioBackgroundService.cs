using GtViewer.Web.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GtViewer.Web.Services;

public class InventarioBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMeshCentralClient _meshClient;
    private readonly MeshCentralOptions _options;
    private readonly ILogger<InventarioBackgroundService> _logger;

    public InventarioBackgroundService(
        IServiceProvider serviceProvider,
        IMeshCentralClient meshClient,
        IOptions<MeshCentralOptions> options,
        ILogger<InventarioBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _meshClient = meshClient;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[InventarioService] Iniciando serviço de inventário em segundo plano (intervalo: {Interval} min)...",
            _options.InventorySyncIntervalMinutes);

        // Inscreve-se nos eventos em tempo real do MeshCentralClient
        _meshClient.NodeConnected += OnNodeConnected;
        _meshClient.NodeChanged += OnNodeChanged;
        _meshClient.FullSyncCompleted += OnFullSyncCompleted;

        try
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.InventorySyncIntervalMinutes));
            using var timer = new PeriodicTimer(interval);

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_meshClient.IsConnected)
                {
                    _logger.LogInformation("[InventarioService] Executando ciclo periódico de sincronização de inventário ({Minutes} min)...",
                        _options.InventorySyncIntervalMinutes);
                    try
                    {
                        var nodes = await _meshClient.GetNodesAsync(TimeSpan.FromSeconds(30), stoppingToken);
                        using var scope = _serviceProvider.CreateScope();
                        var inventarioService = scope.ServiceProvider.GetRequiredService<IInventarioService>();
                        await inventarioService.FullSyncAsync(nodes, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "[InventarioService] Erro ao sincronizar inventário no ciclo periódico.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal
        }
        finally
        {
            _meshClient.NodeConnected -= OnNodeConnected;
            _meshClient.NodeChanged -= OnNodeChanged;
            _meshClient.FullSyncCompleted -= OnFullSyncCompleted;
        }
    }

    private void OnNodeConnected(object? sender, Models.MeshCentral.MeshNodeEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var inventarioService = scope.ServiceProvider.GetRequiredService<IInventarioService>();
                await inventarioService.ProcessNodeUpdateAsync(e.NodeId, e.RawData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InventarioService] Falha ao processar evento NodeConnected para {NodeId}", e.NodeId);
            }
        });
    }

    private void OnNodeChanged(object? sender, Models.MeshCentral.MeshNodeEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var inventarioService = scope.ServiceProvider.GetRequiredService<IInventarioService>();
                await inventarioService.ProcessNodeUpdateAsync(e.NodeId, e.RawData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InventarioService] Falha ao processar evento NodeChanged para {NodeId}", e.NodeId);
            }
        });
    }

    private void OnFullSyncCompleted(object? sender, Models.MeshCentral.FullSyncEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var inventarioService = scope.ServiceProvider.GetRequiredService<IInventarioService>();
                await inventarioService.FullSyncAsync(e.Nodes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InventarioService] Falha ao processar FullSyncCompleted do MeshCentral.");
            }
        });
    }
}
