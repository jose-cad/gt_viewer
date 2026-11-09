namespace GtViewer.Web.Services;

public record ResultadoInicioSessao(
    string SessionId,
    string RemoteDesktopUrl,
    string LoginToken,
    string MirrorUsername,
    string NodeId,
    string UsuarioAlvo,
    int AcaoId);

public interface ISuporteRemotoService
{
    /// <summary>
    /// Inicia uma sessão de suporte remoto (J5, 2.6).
    /// Valida permissão, conectividade, presença de usuário logado e adquire trava atômica.
    /// Concede bitmask mínimo 8 ao usuário espelho e gera token temporário de 2 min.
    /// Invariante 5: grava Ação e Auditoria na mesma transação antes de qualquer efeito externo.
    /// </summary>
    Task<ResultadoInicioSessao> IniciarSessaoAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renova a trava atômica a cada 30 segundos (expira em 90s sem renovação).
    /// Derruba a sessão se o limite de 60 minutos for atingido ou se o técnico for desativado.
    /// </summary>
    Task<bool> RenovarHeartbeatAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Encerra ativamente a sessão de tela no MeshCentral, revoga direitos e libera a trava no banco.
    /// A trava só é liberada após a confirmação da derrubada e revogação.
    /// </summary>
    Task EncerrarSessaoAsync(
        string sessionId,
        string motivo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra recusa ou falta de resposta em 60s do usuário local (msgid: 34).
    /// Libera a trava e revoga os direitos imediatamente.
    /// </summary>
    Task RegistrarRecusaAsync(
        string sessionId,
        string motivo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Encerra sessão quando evento do Windows indica logoff, bloqueio de tela (Win+L) ou encerramento pelo usuário.
    /// </summary>
    Task EncerrarSessaoPorEventoUsuarioAsync(
        string nodeId,
        string motivo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotina de reconciliação: revoga direitos e derruba sessões de travas expiradas ou órfãs na subida e a cada 5 min.
    /// </summary>
    Task ReconciliarTravasOrfasAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Encerra imediatamente qualquer sessão ativa de suporte remoto quando um técnico é desativado.
    /// </summary>
    Task EncerrarSessoesTecnicoDesativadoAsync(
        string tecnicoUsername,
        CancellationToken cancellationToken = default);
}
