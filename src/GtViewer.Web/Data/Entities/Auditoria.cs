namespace GtViewer.Web.Data.Entities;

public class Auditoria
{
    public long Id { get; set; }

    public DateTimeOffset DataHora { get; set; } = DateTimeOffset.UtcNow;
    public required string Ator { get; set; } // Username do técnico ou "sistema"
    public required string Tipo { get; set; } // Lista fechada de convenções (J10)
    public string? Alvo { get; set; } // Identificador do alvo (número de série, máquina, etc.)

    public int? MaquinaId { get; set; }
    public int? AcaoId { get; set; }

    public bool Sucesso { get; set; } = true;
    public string? DetalhesJson { get; set; } // Metadados estruturados (NUNCA segredos!)
    public string? IpOrigem { get; set; }
}
