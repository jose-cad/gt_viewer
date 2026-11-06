namespace GtViewer.Web.Data.Entities;

public class ContratoSerial
{
    public long Id { get; set; }

    public int ContratoImportacaoId { get; set; }
    public ContratoImportacao ContratoImportacao { get; set; } = null!;

    public required string NumeroSerie { get; set; } // Série normalizada (maiúsculas, sem espaços)
    public int LinhaPlanilha { get; set; }
}
