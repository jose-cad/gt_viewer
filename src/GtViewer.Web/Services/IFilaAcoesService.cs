using GtViewer.Web.Data.Entities;

namespace GtViewer.Web.Services;

public interface IFilaAcoesService
{
    /// <summary>
    /// Enfileira uma ação na tabela Acoes com sua prioridade correspondente e grava em Auditoria na mesma transação.
    /// Invariante 5: Se a gravação falhar, a transação sofre rollback e a ação não é enviada.
    /// </summary>
    Task<Acao> EnfileirarAcaoAsync(
        int maquinaId,
        string tipo,
        string ator,
        string? parametrosJson = null,
        int? prioridade = null,
        int? tarefaId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancela uma ação pendente ou em resultado_desconhecido, gravando em Auditoria na mesma transação.
    /// </summary>
    Task<bool> CancelarAcaoAsync(
        int acaoId,
        string ator,
        string? motivo = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processa a máquina especificada: reconcilia resultado_desconhecido e envia a próxima ação pendente.
    /// Garante exclusão mútua: nenhuma ação pendente é enviada enquanto houver outra em enviada ou resultado_desconhecido.
    /// </summary>
    Task ProcessarMaquinaAsync(
        int maquinaId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processa máquina a partir do seu NodeId (usado em eventos NodeConnected).
    /// </summary>
    Task ProcessarNodeConnectedAsync(
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transiciona imediatamente qualquer ação enviada desta máquina para resultado_desconhecido quando ela desconecta.
    /// </summary>
    Task TratarDesconexaoMaquinaAsync(
        string nodeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcilia ações órfãs na subida do sistema (qualquer ação em enviada vira resultado_desconhecido).
    /// </summary>
    Task ReconciliarAcoesOrfasNaSubidaAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executa um ciclo completo de verificação na fila: timeouts (>2min/30min), retentativa de OCUPADO (>30s) e despacho.
    /// </summary>
    Task ProcessarCicloFilaAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcilia todas as máquinas online que possuam ações em resultado_desconhecido (ciclo periódico de 5 minutos).
    /// </summary>
    Task ReconciliarMaquinasOnlineAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cria uma Tarefa para um grupo de máquinas com uma Acao por máquina na mesma transação.
    /// </summary>
    Task<Tarefa> CriarTarefaGrupoAsync(
        int grupoId,
        string tipo,
        string ator,
        int? appCatalogoId = null,
        string? parametrosJson = null,
        CancellationToken cancellationToken = default);
}
