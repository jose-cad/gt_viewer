namespace GtViewer.Web.Data.Entities;

public class Tecnico
{
    public int Id { get; set; }

    public required string UsuarioDominio { get; set; } // Username no AD (ex.: joao.silva)
    public required string NomeExibicao { get; set; }
    public string? Email { get; set; }
    public bool Ativo { get; set; } = true;

    // Permissões do técnico (armazenadas como texto delimitado ou flags):
    // suporte_remoto, reiniciar, bloquear, revelar_senha_resgate, instalar_apps,
    // gerenciar_catalogo, gerenciar_grupos, importar_planilha, cadastrar_maquinas,
    // ver_auditoria, administrar_tecnicos
    public string Permissoes { get; set; } = string.Empty;

    public string? CodigoAtivacaoHash { get; set; } // Hash SHA-256 de código de 16 caracteres
    public DateTimeOffset? CodigoAtivacaoCriadoEm { get; set; } // Válido por 24 h
    public int CodigoAtivacaoTentativas { get; set; } = 0; // Inválido após 5 tentativas erradas

    public string? TotpSecretCifrado { get; set; } // Segredo TOTP cifrado
    public bool TotpConfigurado { get; set; } = false;

    public DateTimeOffset? UltimoLoginEm { get; set; }
    public int TentativasFalhas { get; set; } = 0;
    public DateTimeOffset? TravadoAte { get; set; } // Travamento temporário por 15 min após 5 erros

    // Conta de Emergência (Invariante 6 e Tarefa 6)
    public bool IsContaEmergencia { get; set; } = false;
    public string? SenhaLocalHash { get; set; } // Hash de senha para conta local de emergência
    public DateTimeOffset? EmergenciaExpiraEm { get; set; } // Expira em até 24h após ser habilitada

    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AtualizadoEm { get; set; } = DateTimeOffset.UtcNow;

    public bool PossuiPermissao(string permissao)
    {
        if (string.IsNullOrWhiteSpace(Permissoes)) return false;
        var parts = Permissoes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Contains(permissao, StringComparer.OrdinalIgnoreCase);
    }
}
