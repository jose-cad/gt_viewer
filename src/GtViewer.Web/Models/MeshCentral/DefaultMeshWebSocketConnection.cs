using System.Net.WebSockets;
using System.Text;

namespace GtViewer.Web.Models.MeshCentral;

public class DefaultMeshWebSocketConnection : IMeshWebSocketConnection
{
    private ClientWebSocket? _ws;

    public WebSocketState State => _ws?.State ?? WebSocketState.None;

    public async Task ConnectAsync(Uri uri, string username, string password, bool ignoreSslErrors, CancellationToken cancellationToken)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();

        if (ignoreSslErrors)
        {
            _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(username)) + "," +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        _ws.Options.SetRequestHeader("x-meshauth", authValue);

        await _ws.ConnectAsync(uri, cancellationToken);
    }

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (_ws == null) throw new InvalidOperationException("WebSocket não foi inicializado.");
        return _ws.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    }

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_ws == null) throw new InvalidOperationException("WebSocket não foi inicializado.");
        return _ws.ReceiveAsync(buffer, cancellationToken);
    }

    public async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            await _ws.CloseAsync(closeStatus, statusDescription, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
                }
            }
            catch
            {
                // Ignora exceções durante descarte
            }
            _ws.Dispose();
            _ws = null;
        }
    }
}
