using System.Text.Json.Nodes;

namespace GtViewer.Web.Models.MeshCentral;

public class MeshNodeEventArgs : EventArgs
{
    public string NodeId { get; }
    public JsonNode? RawData { get; }
    public DateTime Timestamp { get; }

    public MeshNodeEventArgs(string nodeId, JsonNode? rawData = null, DateTime? timestamp = null)
    {
        NodeId = nodeId;
        RawData = rawData;
        Timestamp = timestamp ?? DateTime.UtcNow;
    }
}
