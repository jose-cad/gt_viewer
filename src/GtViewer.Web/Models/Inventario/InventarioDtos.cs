namespace GtViewer.Web.Models.Inventario;

public class MaquinaFilterDto
{
    public string? Busca { get; set; }
    public string? ConexaoEstado { get; set; }
    public string? BloqueioEstado { get; set; }
    public string? SituacaoContrato { get; set; }
    public int? GrupoId { get; set; }
    public bool ApenasEmAprovacao { get; set; } = false;
    public bool ApenasSemSerie { get; set; } = false;
}

public class MaquinaListItemDto
{
    public int Id { get; set; }
    public required string NumeroSerie { get; set; }
    public bool SerieManual { get; set; }
    public string? NodeId { get; set; }
    public string? Hostname { get; set; }
    public string? Modelo { get; set; }
    public string? Fabricante { get; set; }
    public string? SistemaOperacional { get; set; }
    public string? UsuariosLogados { get; set; }
    public string ConexaoEstado { get; set; } = "offline";
    public string BloqueioEstado { get; set; } = "desbloqueada";
    public string SituacaoContrato { get; set; } = "sem_planilha";
    public string AntivirusStatus { get; set; } = "Desconhecido";
    public DateTimeOffset? UltimoContatoEm { get; set; }
    public bool EmAprovacao { get; set; }
    public string? GrupoNome { get; set; }
}

public class MaquinaDetailDto
{
    public int Id { get; set; }
    public required string NumeroSerie { get; set; }
    public bool SerieManual { get; set; }
    public string? NodeId { get; set; }
    public string? Hostname { get; set; }
    public string? Modelo { get; set; }
    public string? Fabricante { get; set; }
    public string? SistemaOperacional { get; set; }
    public string? Arquitetura { get; set; }
    public string? AntivirusNome { get; set; }
    public bool? AntivirusAtivo { get; set; }
    public bool? AntivirusAtualizado { get; set; }
    public string AntivirusStatus { get; set; } = "Desconhecido";
    public string? UsuariosLogadosJson { get; set; }
    public string ConexaoEstado { get; set; } = "offline";
    public string BloqueioEstado { get; set; } = "desbloqueada";
    public string? MensagemBloqueio { get; set; }
    public string SituacaoContrato { get; set; } = "sem_planilha";
    public DateTimeOffset? PrimeiroContatoEm { get; set; }
    public DateTimeOffset? UltimoContatoEm { get; set; }

    public bool EmAprovacao { get; set; }
    public string? CandidatoNodeId { get; set; }
    public string? CandidatoHostname { get; set; }
    public string? CandidatoIp { get; set; }
    public DateTimeOffset? CandidatoPrimeiroContatoEm { get; set; }
    public DateTimeOffset? CandidatoUltimoContatoEm { get; set; }

    public int? GrupoId { get; set; }
    public string? GrupoNome { get; set; }

    public List<HistoricoUsuarioDto> HistoricoUsuarios { get; set; } = new();
    public List<AcaoResumoDto> AcoesPendentes { get; set; } = new();
    public List<AuditoriaResumoDto> UltimasAuditorias { get; set; } = new();
}

public record HistoricoUsuarioDto(long Id, string Usuario, DateTimeOffset InicioEm, DateTimeOffset? FimEm);
public record AcaoResumoDto(int Id, string Tipo, int Prioridade, string Estado, string Ator, DateTimeOffset CriadoEm);
public record AuditoriaResumoDto(long Id, DateTimeOffset DataHora, string Ator, string Tipo, bool Sucesso, string? Alvo);

public record AprovarMaquinaResult(bool Sucesso, string? MensagemErro = null);
