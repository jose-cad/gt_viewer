using System.Text.Json.Nodes;
using GtViewer.Web.Models.Inventario;

namespace GtViewer.Web.Services;

public interface IInventarioService
{
    Task ProcessNodeUpdateAsync(string nodeId, JsonNode? nodeData, CancellationToken cancellationToken = default);
    Task FullSyncAsync(IReadOnlyDictionary<string, JsonNode> nodes, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MaquinaListItemDto>> ListMaquinasAsync(MaquinaFilterDto filter, CancellationToken cancellationToken = default);
    Task<MaquinaDetailDto?> GetMaquinaDetailAsync(int id, CancellationToken cancellationToken = default);

    Task<AprovarMaquinaResult> AprovarTrocaAgenteAsync(
        int maquinaId,
        string tecnicoUsername,
        bool manterBloqueio,
        string? justificativaLimpezaBloqueio,
        bool tecnicoPodeBloquear,
        CancellationToken cancellationToken = default);

    Task<bool> RejeitarTrocaAgenteAsync(int maquinaId, string tecnicoUsername, CancellationToken cancellationToken = default);

    Task<bool> CorrigirSerieManualAsync(int maquinaId, string novaSerie, string tecnicoUsername, CancellationToken cancellationToken = default);

    Task<bool> UnirRegistrosAsync(
        int maquinaOrigemId,
        int maquinaDestinoId,
        string tecnicoUsername,
        bool manterBloqueio,
        string? justificativaLimpezaBloqueio,
        bool tecnicoPodeBloquear,
        CancellationToken cancellationToken = default);

    Task RegistrarDownloadInstaladorAsync(string tecnicoUsername, string? ipOrigem, CancellationToken cancellationToken = default);
}
