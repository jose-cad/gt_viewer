using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Tecnicos;

[Authorize(Policy = Permissoes.AdministrarTecnicos)]
public class EditarModel : PageModel
{
    private readonly ITecnicoService _tecnicoService;

    public EditarModel(ITecnicoService tecnicoService)
    {
        _tecnicoService = tecnicoService;
    }

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public string UsuarioDominio { get; set; } = string.Empty;
    public string TecnicoNome { get; set; } = string.Empty;

    [BindProperty]
    public List<string> PermissoesSelecionadas { get; set; } = [];

    public string? ErrorMessage { get; set; }

    public List<PermissaoInfo> TodasPermissoes =>
    [
        new(Permissoes.SuporteRemoto, "Suporte Remoto", "Acessar tela remota das máquinas Windows com consentimento do usuário."),
        new(Permissoes.Reiniciar, "Reiniciar Máquinas", "Disparar reinício preventivo com aviso de 2 minutos."),
        new(Permissoes.Bloquear, "Bloquear e Desbloquear", "Impor tela de bloqueio e desativar contas de domínio ou restaurar acesso."),
        new(Permissoes.RevelarSenhaResgate, "Revelar Senha de Resgate", "Visualizar senha de contingência da máquina offline com TOTP reforçado."),
        new(Permissoes.InstalarApps, "Instalar Aplicativos", "Distribuir e remover softwares homologados nas máquinas."),
        new(Permissoes.GerenciarCatalogo, "Gerenciar Catálogo", "Cadastrar instaladores, parâmetros e regras de detecção de apps."),
        new(Permissoes.GerenciarGrupos, "Gerenciar Grupos", "Criar, alterar e organizar grupos de máquinas."),
        new(Permissoes.ImportarPlanilha, "Importar Planilha", "Fazer upload da planilha da locadora e atualizar conciliação."),
        new(Permissoes.CadastrarMaquinas, "Cadastrar Máquinas", "Baixar instalador do agente, resolver conflitos de série e aprovar trocas."),
        new(Permissoes.VerAuditoria, "Ver Auditoria", "Consultar logs e histórico completo de ações de segurança."),
        new(Permissoes.AdministrarTecnicos, "Administrar Técnicos", "Criar técnicos, conceder permissões e redefinir 2FA.")
    ];

    public async Task<IActionResult> OnGetAsync()
    {
        var tecnico = await _tecnicoService.ObterPorIdAsync(Id);
        if (tecnico == null)
        {
            return NotFound();
        }

        UsuarioDominio = tecnico.UsuarioDominio;
        TecnicoNome = tecnico.NomeExibicao;

        if (!string.IsNullOrWhiteSpace(tecnico.Permissoes))
        {
            PermissoesSelecionadas = tecnico.Permissoes
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ator = User.Identity?.Name ?? "sistema";

        try
        {
            var tecnico = await _tecnicoService.AtualizarPermissoesAsync(Id, PermissoesSelecionadas, ator);
            TempData["MensagemSucesso"] = $"Permissões do técnico '{tecnico.UsuarioDominio}' atualizadas com sucesso.";
            return RedirectToPage("/Tecnicos/Index");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            var tecnico = await _tecnicoService.ObterPorIdAsync(Id);
            if (tecnico != null)
            {
                UsuarioDominio = tecnico.UsuarioDominio;
                TecnicoNome = tecnico.NomeExibicao;
            }
            return Page();
        }
    }
}
