using GtViewer.Web.Data.Entities;

namespace GtViewer.Web.Services;

public interface IBloqueioService
{
    /// <summary>
    /// Solicita o bloqueio da máquina com justificativa/motivo obrigatório.
    /// Exige permissão 'bloquear'.
    /// </summary>
    Task SolicitarBloqueioAsync(
        int maquinaId,
        string tecnicoUsername,
        string motivo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Solicita o desbloqueio da máquina. Se houver ação de bloqueio ainda 'pendente', apenas cancela a pendência.
    /// Se já tiver sido enviada ou a máquina estiver em falha/bloqueada, enfileira o desbloqueio.
    /// Exige permissão 'bloquear'.
    /// </summary>
    Task SolicitarDesbloqueioAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Avalia se a senha de resgate pode ser revelada no momento para a máquina.
    /// Condições: MeshCentral ativo, máquina em bloqueada/desbloqueio_pendente/falha ou bloqueio_pendente (já enviada/desconhecida), e offline há >5 min.
    /// </summary>
    bool PodeRevelarSenha(Maquina maquina, out string? motivoIndisponivel);

    /// <summary>
    /// Revela a senha de resgate após conferência de um novo código TOTP do técnico solicitante.
    /// Marca a senha como revelada e grava em Auditoria (sem a senha).
    /// Exige permissão 'revelar_senha_resgate'.
    /// </summary>
    Task<string> RevelarSenhaResgateAsync(
        int maquinaId,
        string tecnicoUsername,
        string totpCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Agenda a troca da senha de resgate por uma nova senha candidata. Recusado se já houver candidata pendente.
    /// </summary>
    Task AgendarTrocaSenhaResgateAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Agenda a redefinição da conta de resgate gerando uma nova senha candidata e garantindo o usuário.
    /// </summary>
    Task AgendarRedefinirResgateAsync(
        int maquinaId,
        string tecnicoUsername,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna a senha em texto puro para o agente Windows a partir do token Bearer único de /agente/senha-resgate.
    /// Valida expiração e uso único (não reutilizável).
    /// </summary>
    Task<string?> ObterSenhaAgentePorTokenAsync(
        string bearerToken,
        CancellationToken cancellationToken = default);
}
