using System.Text.Json.Nodes;
using GtViewer.Web.Models.MeshCentral;

namespace GtViewer.Web.Services;

public interface IMeshCentralClient : IAsyncDisposable
{
    /// <summary>
    /// Indica se a conexão WebSocket com o control.ashx do MeshCentral está ativa.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Último snapshot sincronizado de dispositivos/nós obtido do MeshCentral.
    /// </summary>
    IReadOnlyDictionary<string, JsonNode> CachedNodes { get; }

    /// <summary>
    /// Evento disparado quando o estado da conexão muda (true = conectado, false = desconectado).
    /// </summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Evento disparado quando uma máquina se conecta ao MeshCentral (nodeconnect).
    /// </summary>
    event EventHandler<MeshNodeEventArgs>? NodeConnected;

    /// <summary>
    /// Evento disparado quando uma máquina se desconecta do MeshCentral (nodedisconnect).
    /// </summary>
    event EventHandler<MeshNodeEventArgs>? NodeDisconnected;

    /// <summary>
    /// Evento disparado quando uma máquina tem seus dados ou estado alterados (changenode).
    /// </summary>
    event EventHandler<MeshNodeEventArgs>? NodeChanged;

    /// <summary>
    /// Evento disparado imediatamente após cada sincronização completa (na subida e após cada reconexão).
    /// </summary>
    event EventHandler<FullSyncEventArgs>? FullSyncCompleted;

    /// <summary>
    /// Evento para escuta de mensagens brutas JSON recebidas pelo WebSocket.
    /// </summary>
    event EventHandler<string>? RawMessageReceived;

    /// <summary>
    /// Inicia manualmente a conexão e a rotina persistente com o MeshCentral.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Encerra a conexão WebSocket com o MeshCentral de forma graciosa.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Envia uma requisição JSON arbitrada correlacionando a resposta via 'responseid'.
    /// Suporta chamadas simultâneas/concorrentes seguras.
    /// </summary>
    Task<JsonNode?> SendRequestAsync(object payload, string? responseId = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consulta os metadados do servidor MeshCentral (versão, features, consentimento).
    /// </summary>
    Task<JsonNode?> GetServerInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtém todos os computadores/nós cadastrados no MeshCentral.
    /// </summary>
    Task<IReadOnlyDictionary<string, JsonNode>> GetNodesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtém detalhes aprofundados de hardware e sistema operacional de um nó (sysinfo).
    /// </summary>
    Task<JsonNode?> GetSysInfoAsync(string nodeId, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executa um script PowerShell fixo e homologado como NT AUTHORITY\SYSTEM.
    /// Retorna GTV-RESULT e GTV-EXIT conforme a especificação.
    /// </summary>
    Task<RunCommandResult> RunFixedScriptAsync(string nodeId, string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Garante que o usuário espelho técnico existe no MeshCentral sem privilégios administrativos.
    /// </summary>
    Task<string> EnsureMirrorUserAsync(string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Concede permissão estrita de visualização/controle de tela (bitmask 0x08) para o usuário espelho em um nó específico.
    /// </summary>
    Task<bool> GrantRemoteDesktopRightsAsync(string nodeId, string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoga todos os direitos do usuário espelho sobre o dispositivo.
    /// </summary>
    Task<bool> RevokeRemoteDesktopRightsAsync(string nodeId, string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Derruba imediatamente todas as sessões ativas do usuário espelho no MeshCentral.
    /// </summary>
    Task<bool> DropUserSessionsAsync(string technicianUsername, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envia notificação toast na tela do usuário logado.
    /// </summary>
    Task<bool> SendToastAsync(string nodeId, string title, string message, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Solicita reinício preventivo da máquina com aviso ao usuário. Recusado se o MeshCentral estiver desconectado.
    /// </summary>
    Task<RunCommandResult> RequestRestartAsync(string nodeId, TimeSpan? delay = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
