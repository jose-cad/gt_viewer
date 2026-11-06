namespace GtViewer.Web.Data.Entities;

public class AppCatalogo
{
    public int Id { get; set; }

    public required string Nome { get; set; }
    public required string Versao { get; set; }
    public required string FabricanteEsperado { get; set; } // Nome esperado no certificado digital (Authenticode Subject/Publisher)
    public required string HashSha256 { get; set; } // Hash SHA-256 do instalador hospedado no servidor

    public required string NomeArquivo { get; set; }
    public long TamanhoBytes { get; set; }

    public string ParametrosInstalacao { get; set; } = string.Empty; // Parâmetros silenciosos (ex.: /quiet /norestart)
    public string ComandoRemocao { get; set; } = string.Empty; // Comando de desinstalação

    public string RegraDeteccaoTipo { get; set; } = "registro"; // "registro" ou "arquivo"
    public required string RegraDeteccaoChave { get; set; }
    public string? RegraDeteccaoValor { get; set; }

    public bool Ativo { get; set; } = true;

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AtualizadoEm { get; set; } = DateTimeOffset.UtcNow;
}
