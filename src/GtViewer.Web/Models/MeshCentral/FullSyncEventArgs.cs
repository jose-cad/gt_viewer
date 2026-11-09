using System.Text.Json.Nodes;

namespace GtViewer.Web.Models.MeshCentral;

public class FullSyncEventArgs : EventArgs
{
    public IReadOnlyDictionary<string, JsonNode> Nodes { get; }
    public DateTime Timestamp { get; }

    public FullSyncEventArgs(IReadOnlyDictionary<string, JsonNode> nodes, DateTime? timestamp = null)
    {
        Nodes = nodes;
        Timestamp = timestamp ?? DateTime.UtcNow;
    }
}
