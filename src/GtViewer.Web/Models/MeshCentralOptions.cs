namespace GtViewer.Web.Models;

public class MeshCentralOptions
{
    public const string SectionName = "MeshCentral";

    public string ServerUrl { get; set; } = "https://mesh.localhost";
    public string WebSocketUrl { get; set; } = "wss://mesh.localhost/control.ashx";
    public string ServiceUsername { get; set; } = "admin";
    public string ServicePassword { get; set; } = "admin_dev_password_123";
    public string LoginCookieEncryptionKey { get; set; } = "9ae4fb0b32a70e6fecb808f07ed6259ecb5336327b9aef2ac2c46b9d0b0f10a3ddfa47147b4c6dfe484a2042e255b07def08ca68d26c4ad0bc782ead178700525f3b707ab9493c940ff40b9cddfdc3c0";
    public int DefaultExpireMinutes { get; set; } = 2;
    public int ReconnectIntervalSeconds { get; set; } = 2;
    public bool AutoConnect { get; set; } = true;
    public bool IgnoreSslErrors { get; set; } = true;
}
