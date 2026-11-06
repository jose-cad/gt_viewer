namespace GtViewer.Web.Data.Entities;

public class Maquina
{
    public int Id { get; set; }

    public required string NumeroSerie { get; set; } // Identidade da máquina (Invariante 7), índice único
    public bool SerieManual { get; set; } = false; // Se definida manualmente em J2, não é sobrescrita por bios_serial

    public string? NodeId { get; set; } // MeshCentral nodeId (ex.: node//...)
    public string? Hostname { get; set; }
    public string? Modelo { get; set; } // product_name
    public string? Fabricante { get; set; } // chassis_manufacturer ou board_vendor
    public string? SistemaOperacional { get; set; } // osdesc
    public string? Arquitetura { get; set; } // 64-bit

    public string? AntivirusNome { get; set; }
    public bool? AntivirusAtivo { get; set; }
    public bool? AntivirusAtualizado { get; set; }

    public string? UsuariosLogadosJson { get; set; } // JSON array dos usuários interativos logados

    // Estados independentes (Convenções):
    // Conexao: online, offline, sumida
    public string ConexaoEstado { get; set; } = "offline";

    // Bloqueio: desbloqueada, bloqueio_pendente, bloqueada, desbloqueio_pendente, falha
    public string BloqueioEstado { get; set; } = "desbloqueada";
    public string? MensagemBloqueio { get; set; }

    public DateTimeOffset? UltimoContatoEm { get; set; }
    public DateTimeOffset? PrimeiroContatoEm { get; set; }

    // Conflito de série (J2): agente em "Aguardando aprovação"
    public bool EmAprovacao { get; set; } = false;
    public string? CandidatoNodeId { get; set; }
    public string? CandidatoHostname { get; set; }
    public string? CandidatoIp { get; set; }
    public DateTimeOffset? CandidatoPrimeiroContatoEm { get; set; }
    public DateTimeOffset? CandidatoUltimoContatoEm { get; set; }

    // Senhas de resgate únicas por máquina (cifradas)
    public string? SenhaResgateAtualCifrada { get; set; }
    public string? SenhaResgateCandidataCifrada { get; set; }
    public bool SenhaResgateRevelada { get; set; } = false;

    public int? GrupoId { get; set; }
    public Grupo? Grupo { get; set; }

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AtualizadoEm { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<HistoricoUsuarioMaquina> HistoricoUsuarios { get; set; } = new List<HistoricoUsuarioMaquina>();
    public ICollection<Acao> Acoes { get; set; } = new List<Acao>();
    public TravaSessao? TravaSessao { get; set; }
}
