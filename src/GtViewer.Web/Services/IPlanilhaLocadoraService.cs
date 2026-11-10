using GtViewer.Web.Data.Entities;

namespace GtViewer.Web.Services;

public record PreviaPlanilhaResult(
    int ImportacaoId,
    string NomeArquivo,
    int TotalLinhas,
    int SeriesValidas,
    int SeriesRepetidas,
    int LinhasIgnoradas,
    bool ExigeConfirmacaoReforcada,
    double QuedaPercentual,
    int SeriesVigenteAnterior,
    List<string> SeriesAmostra
);

public record MaquinaConferenciaItem(
    string NumeroSerie,
    string? Hostname,
    string? Modelo,
    string? Fabricante,
    string? ConexaoEstado,
    DateTimeOffset? UltimoContatoEm
);

public record RelatorioConferenciaResult(
    ContratoImportacao? ContratoVigente,
    List<MaquinaConferenciaItem> NoContratoENoInventario,
    List<MaquinaConferenciaItem> NoContratoENuncaApareceuOuSumida,
    List<MaquinaConferenciaItem> NoInventarioEForaDoContrato
);

public interface IPlanilhaLocadoraService
{
    Task<PreviaPlanilhaResult> ProcessarPreviaAsync(
        Stream stream,
        string nomeArquivo,
        long tamanhoBytes,
        string ator,
        CancellationToken cancellationToken = default);

    Task<ContratoImportacao> ConfirmarImportacaoAsync(
        int importacaoId,
        string ator,
        bool confirmacaoReforcadaAceita = false,
        CancellationToken cancellationToken = default);

    Task<ContratoImportacao?> ObterContratoVigenteAsync(
        CancellationToken cancellationToken = default);

    Task<List<ContratoImportacao>> ListarHistoricoImportacoesAsync(
        CancellationToken cancellationToken = default);

    Task<RelatorioConferenciaResult> ObterRelatorioConferenciaAsync(
        CancellationToken cancellationToken = default);
}
