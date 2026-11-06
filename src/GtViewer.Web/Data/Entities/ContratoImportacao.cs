namespace GtViewer.Web.Data.Entities;

public class ContratoImportacao
{
    public int Id { get; set; }

    public required string NomeArquivo { get; set; }
    public int TotalLinhas { get; set; }
    public int SeriesValidas { get; set; }
    public int SeriesRepetidas { get; set; }
    public int LinhasIgnoradas { get; set; }

    public bool Confirmado { get; set; } = false;
    public bool Vigente { get; set; } = false; // Apenas a mais recente confirmada fica como vigente

    public required string Ator { get; set; } // Username do técnico

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConfirmadoEm { get; set; }

    public ICollection<ContratoSerial> Seriais { get; set; } = new List<ContratoSerial>();
}
