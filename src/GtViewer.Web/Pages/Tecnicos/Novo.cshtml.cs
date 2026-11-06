using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Tecnicos;

public record PermissaoInfo(string Chave, string Nome, string Descricao);

[Authorize(Policy = Permissoes.AdministrarTecnicos)]
public class NovoModel : PageModel
{
    private readonly ITecnicoService _tecnicoService;

    public NovoModel(ITecnicoService tecnicoService)
    {
        _tecnicoService = tecnicoService;
    }

    [BindProperty]
    public string UsuarioDominio { get; set; } = string.Empty;

    [BindProperty]
    public string NomeExibicao { get; set; } = string.Empty;

    [BindProperty]
    public string? Email { get; set; }

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

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ator = User.Identity?.Name ?? "sistema";

        try
        {
            var (tecnico, codigoAtivacao) = await _tecnicoService.CriarTecnicoAsync(
                UsuarioDominio,
                NomeExibicao,
                Email,
                PermissoesSelecionadas,
                ator);

            TempData["MensagemSucesso"] = $"Técnico '{tecnico.UsuarioDominio}' cadastrado com sucesso!";
            TempData["CodigoAtivacaoGerado"] = codigoAtivacao;
            TempData["UsuarioCodigoGerado"] = tecnico.UsuarioDominio;

            return RedirectToPage("/Tecnicos/Index");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
