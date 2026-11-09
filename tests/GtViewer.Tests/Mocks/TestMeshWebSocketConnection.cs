using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GtViewer.Web.Models.MeshCentral;

namespace GtViewer.Tests.Mocks;

public class TestMeshWebSocketConnection : IMeshWebSocketConnection
{
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly List<string> _sentMessages = new();
    private readonly object _sentLock = new();
    private MemoryStream? _pendingReadStream;

    public WebSocketState State { get; set; } = WebSocketState.None;
    public string? ConnectedUsername { get; private set; }
    public string? ConnectedPassword { get; private set; }
    public Uri? ConnectedUri { get; private set; }
    public bool IgnoreSslErrors { get; private set; }

    public IReadOnlyList<string> SentMessages
    {
        get
        {
            lock (_sentLock)
            {
                return _sentMessages.ToList();
            }
        }
    }

    public Func<string, Task>? OnMessageSentAsync { get; set; }

    public Task ConnectAsync(Uri uri, string username, string password, bool ignoreSslErrors, CancellationToken cancellationToken)
    {
        ConnectedUri = uri;
        ConnectedUsername = username;
        ConnectedPassword = password;
        IgnoreSslErrors = ignoreSslErrors;
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (State != WebSocketState.Open)
        {
            throw new WebSocketException(WebSocketError.InvalidState, "WebSocket não está aberto para envio.");
        }

        var json = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
        lock (_sentLock)
        {
            _sentMessages.Add(json);
        }

        if (OnMessageSentAsync != null)
        {
            await OnMessageSentAsync(json);
        }
    }

    public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (State != WebSocketState.Open)
        {
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "Closed");
        }

        if (_pendingReadStream == null || _pendingReadStream.Position >= _pendingReadStream.Length)
        {
            try
            {
                var msg = await _incoming.Reader.ReadAsync(cancellationToken);
                _pendingReadStream = new MemoryStream(Encoding.UTF8.GetBytes(msg));
            }
            catch (ChannelClosedException)
            {
                State = WebSocketState.Closed;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "Closed");
            }
        }

        var readCount = await _pendingReadStream.ReadAsync(buffer.Array!, buffer.Offset, buffer.Count, cancellationToken);
        var endOfMessage = _pendingReadStream.Position >= _pendingReadStream.Length;
        if (endOfMessage)
        {
            _pendingReadStream = null;
        }

        return new WebSocketReceiveResult(readCount, WebSocketMessageType.Text, endOfMessage);
    }

    public void EnqueueIncomingMessage(string json)
    {
        _incoming.Writer.TryWrite(json);
    }

    public void CloseServerSide()
    {
        State = WebSocketState.Closed;
        _incoming.Writer.Complete();
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        State = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        State = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
