using System.Net.WebSockets;

namespace GtViewer.Web.Models.MeshCentral;

public interface IMeshWebSocketConnection : IAsyncDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, string username, string password, bool ignoreSslErrors, CancellationToken cancellationToken);
    Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
    Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);
    Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);
}
