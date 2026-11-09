using GtViewer.Web.Data.Entities;

namespace GtViewer.Web.Services;

public interface IReiniciarService
{
    /// <summary>
    /// Solicita o reinício de um computador Windows com aviso prévio de 2 minutos ao usuário (J7).
    /// Ação imediata: aceita apenas com a máquina online e MeshCentral conectado (reinício não fica pendente).
    /// Registra Acao e Auditoria na mesma transação antes de enviar qualquer comando ao MeshCentral (Invariante 5).
    /// </summary>
    Task<Acao> SolicitarReinicioAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default);
}
