namespace GtViewer.Web.Data.Entities;

public class AlertaEmergencia
{
    public int Id { get; set; }

    public DateTimeOffset DataHora { get; set; } = DateTimeOffset.UtcNow;
    public required string Mensagem { get; set; }
    public required string Ator { get; set; } // Ator que disparou o alerta (ex.: conta de emergência)

    public bool Reconhecido { get; set; } = false;
    public string? ReconhecidoPor { get; set; } // Qualquer administrador pode reconhecer
    public DateTimeOffset? ReconhecidoEm { get; set; }
}
