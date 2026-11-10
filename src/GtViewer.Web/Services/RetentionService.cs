using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GtViewer.Web.Services;

public class RetentionService : IRetentionService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IConfiguration configuration,
        ILogger<RetentionService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<RetentionResult> ExecutarExpurgoAsync(
        DateTimeOffset? referencia = null,
        CancellationToken cancellationToken = default)
    {
        var agora = referencia ?? DateTimeOffset.UtcNow;
        var dataCorte = agora.AddYears(-2);

        var retentionConnStr = _configuration.GetConnectionString("RetentionConnection")
            ?? _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Nenhuma connection string configurada para o serviço de retenção.");

        _logger.LogInformation(
            "[Retenção] Iniciando expurgo de registros anteriores a {Corte} (regra de 2 anos)", dataCorte);

        await using var connection = new NpgsqlConnection(retentionConnStr);
        await connection.OpenAsync(cancellationToken);

        // 1. Excluir registros de Auditorias anteriores ao corte de 2 anos
        await using var cmdAuditoria = new NpgsqlCommand(
            "DELETE FROM \"Auditorias\" WHERE \"DataHora\" < @corte", connection);
        cmdAuditoria.Parameters.AddWithValue("corte", dataCorte);
        var auditoriasExcluidas = await cmdAuditoria.ExecuteNonQueryAsync(cancellationToken);

        // 2. Excluir registros de HistoricoUsuarioMaquinas anteriores ao corte de 2 anos
        // Registros onde o início foi há mais de 2 anos e ou o término já foi há mais de 2 anos ou não houve logout
        await using var cmdHistorico = new NpgsqlCommand(
            "DELETE FROM \"HistoricoUsuarioMaquinas\" WHERE \"InicioEm\" < @corte AND (\"FimEm\" IS NULL OR \"FimEm\" < @corte)", connection);
        cmdHistorico.Parameters.AddWithValue("corte", dataCorte);
        var historicosExcluidos = await cmdHistorico.ExecuteNonQueryAsync(cancellationToken);

        // 3. Excluir registros de Acoes anteriores ao corte de 2 anos
        await using var cmdAcoes = new NpgsqlCommand(
            "DELETE FROM \"Acoes\" WHERE \"CriadoEm\" < @corte", connection);
        cmdAcoes.Parameters.AddWithValue("corte", dataCorte);
        var acoesExcluidas = await cmdAcoes.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "[Retenção] Rotina diária de expurgo concluída com sucesso. Corte: {Corte}. Excluídos: {Auditorias} auditorias, {Historicos} históricos de usuários, {Acoes} ações.",
            dataCorte, auditoriasExcluidas, historicosExcluidos, acoesExcluidas);

        return new RetentionResult(auditoriasExcluidas, historicosExcluidos, acoesExcluidas, dataCorte);
    }
}
