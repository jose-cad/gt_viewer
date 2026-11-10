using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Catalogo;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ICatalogoService _catalogoService;

    public IndexModel(ICatalogoService catalogoService)
    {
        _catalogoService = catalogoService;
    }

    public List<AppCatalogo> Apps { get; set; } = new();

    [TempData]
    public string? MensagemSucesso { get; set; }

    [TempData]
    public string? MensagemErro { get; set; }

    public async Task OnGetAsync()
    {
        Apps = await _catalogoService.ListarAppsAsync();
    }

    public async Task<IActionResult> OnPostDesativarAsync(int id)
    {
        if (!User.HasPermissao(Permissoes.GerenciarCatalogo))
        {
            return Forbid();
        }

        var ator = User.Identity?.Name ?? "desconhecido";
        var sucesso = await _catalogoService.DesativarAppAsync(id, ator);
        if (sucesso)
        {
            MensagemSucesso = "Aplicativo desativado com sucesso.";
        }
        else
        {
            MensagemErro = "Aplicativo não encontrado.";
        }

        return RedirectToPage();
    }
}
