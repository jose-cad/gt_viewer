namespace GtViewer.Web.Data.Entities;

public class Tarefa
{
    public int Id { get; set; }

    public required string Tipo { get; set; } // Ex.: instalacao_grupo, remocao_grupo
    public int? AppCatalogoId { get; set; }
    public AppCatalogo? AppCatalogo { get; set; }

    public int? GrupoId { get; set; }
    public Grupo? Grupo { get; set; }

    public required string Ator { get; set; } // Username do técnico

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConcluidoEm { get; set; }

    public ICollection<Acao> Acoes { get; set; } = new List<Acao>();
}
