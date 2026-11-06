namespace GtViewer.Web.Security;

public static class Permissoes
{
    public const string SuporteRemoto = "suporte_remoto";
    public const string Reiniciar = "reiniciar";
    public const string Bloquear = "bloquear";
    public const string RevelarSenhaResgate = "revelar_senha_resgate";
    public const string InstalarApps = "instalar_apps";
    public const string GerenciarCatalogo = "gerenciar_catalogo";
    public const string GerenciarGrupos = "gerenciar_grupos";
    public const string ImportarPlanilha = "importar_planilha";
    public const string CadastrarMaquinas = "cadastrar_maquinas";
    public const string VerAuditoria = "ver_auditoria";
    public const string AdministrarTecnicos = "administrar_tecnicos";

    public static readonly string[] Todas =
    [
        SuporteRemoto,
        Reiniciar,
        Bloquear,
        RevelarSenhaResgate,
        InstalarApps,
        GerenciarCatalogo,
        GerenciarGrupos,
        ImportarPlanilha,
        CadastrarMaquinas,
        VerAuditoria,
        AdministrarTecnicos
    ];

    // Permissões estritas da conta de emergência (Invariante 6 e Tarefa 6)
    public static readonly string[] Emergencia =
    [
        Bloquear,
        Reiniciar,
        SuporteRemoto
    ];

    public static bool IsAdmin(string? permissoesCsv)
    {
        if (string.IsNullOrWhiteSpace(permissoesCsv)) return false;
        var parts = permissoesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Contains(AdministrarTecnicos, StringComparer.OrdinalIgnoreCase);
    }
}
