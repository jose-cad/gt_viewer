namespace GtViewer.Web.Data.Entities;

public class HistoricoUsuarioMaquina
{
    public long Id { get; set; }

    public int MaquinaId { get; set; }
    public Maquina Maquina { get; set; } = null!;

    public required string Usuario { get; set; }
    public DateTimeOffset InicioEm { get; set; }
    public DateTimeOffset? FimEm { get; set; }

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
}
