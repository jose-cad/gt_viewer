namespace GtViewer.Web.Data.Entities;

public class Grupo
{
    public int Id { get; set; }

    public required string Nome { get; set; }
    public string? Descricao { get; set; }

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AtualizadoEm { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Maquina> Maquinas { get; set; } = new List<Maquina>();
}
