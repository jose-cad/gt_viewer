namespace GtViewer.Web.Services;

public record RetentionResult(
    int AuditoriasExcluidas,
    int HistoricosExcluidos,
    int AcoesExcluidas,
    DateTimeOffset DataCorte);

public interface IRetentionService
{
    /// <summary>
    /// Executa a rotina diária de expurgo de dados com mais de 2 anos (Invariante 12 e Seção 2.10).
    /// Utiliza obrigatoriamente a credencial do usuário dedicado do banco 'gtv_retention'.
    /// </summary>
    /// <param name="referencia">Momento de referência para cálculo dos 2 anos (padrão: UtcNow).</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    Task<RetentionResult> ExecutarExpurgoAsync(DateTimeOffset? referencia = null, CancellationToken cancellationToken = default);
}
