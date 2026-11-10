using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GtViewer.Web.Services;

public class AuditoriaRetentionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AuditoriaRetentionWorker> _logger;
    private readonly TimeSpan _intervalo;

    public AuditoriaRetentionWorker(
        IServiceProvider serviceProvider,
        ILogger<AuditoriaRetentionWorker> logger,
        TimeSpan? intervalo = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _intervalo = intervalo ?? TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[AuditoriaRetentionWorker] Inicializado. Intervalo diário: {Intervalo}", _intervalo);

        // Atraso inicial de 30 segundos após a subida para estabilização do host
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var retentionService = scope.ServiceProvider.GetRequiredService<IRetentionService>();
                await retentionService.ExecutarExpurgoAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AuditoriaRetentionWorker] Falha ao executar rotina diária de retenção de auditoria.");
            }

            try
            {
                await Task.Delay(_intervalo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
