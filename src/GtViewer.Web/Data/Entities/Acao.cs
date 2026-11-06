namespace GtViewer.Web.Data.Entities;

public class Acao
{
    public int Id { get; set; }

    public int MaquinaId { get; set; }
    public Maquina Maquina { get; set; } = null!;

    public int? TarefaId { get; set; }
    public Tarefa? Tarefa { get; set; }

    // Tipo: bloquear, desbloquear, garantir_resgate, trocar_resgate, redefinir_resgate, instalar, remover, reiniciar, suporte_remoto
    public required string Tipo { get; set; }

    // Prioridade na fila (seção 2.4):
    // 1: garantir/redefinir/trocar senha de resgate
    // 2: bloquear/desbloquear
    // 3: instalar/remover
    public int Prioridade { get; set; } = 2;

    // Estado da ação (Convenções): pendente, enviada, executada, falhou, resultado_desconhecido, cancelada
    public string Estado { get; set; } = "pendente";

    public required string Ator { get; set; } // "sistema" ou username do técnico
    public string? ParametrosJson { get; set; } // Parâmetros em JSON (NUNCA segredos!)

    public string? Resultado { get; set; } // GTV-RESULT (ex.: OK, JA_INSTALADO, BLOQUEADA)
    public int? CodigoSaida { get; set; } // GTV-EXIT (0 = sucesso)
    public string? Detalhes { get; set; }
    public int Tentativas { get; set; } = 0;

    // Token temporário em /agente/* (senha de resgate ou instalador)
    public string? TokenAgenteHash { get; set; } // Hash SHA-256 do token
    public DateTimeOffset? TokenAgenteExpiraEm { get; set; }
    public bool TokenAgenteUsado { get; set; } = false;

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EnviadoEm { get; set; }
    public DateTimeOffset? ConcluidoEm { get; set; }
}
