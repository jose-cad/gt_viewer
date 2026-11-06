using System.Text.Json.Serialization;

namespace GtViewer.Web.Models;

public class MeshLoginTokenPayload
{
    [JsonPropertyName("u")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("a")]
    public int AuthType { get; set; } = 3; // 3 = Login token

    [JsonPropertyName("expire")]
    public int ExpireMinutes { get; set; } = 2; // 2 minutos

    [JsonPropertyName("time")]
    public long Time { get; set; } // Unix timestamp em segundos
}
