namespace GtViewer.Web.Data.Entities;

public class TravaSessao
{
    public int Id { get; set; }

    public int MaquinaId { get; set; }
    public Maquina Maquina { get; set; } = null!;

    public required string TecnicoUsername { get; set; }
    public required string IdentificadorSessao { get; set; } // Identificador único da sessão
    public required string UsuarioLogadoNoMomento { get; set; } // Usuário do Windows que consentiu

    public DateTimeOffset AdquiridaEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UltimoHeartbeatEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiraEm { get; set; } // Renovada a cada 30s, expira sem renovação em 90s
}
