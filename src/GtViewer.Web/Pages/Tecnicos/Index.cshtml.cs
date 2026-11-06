using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Tecnicos;

[Authorize(Policy = Permissoes.AdministrarTecnicos)]
public class IndexModel : PageModel
{
    private readonly ITecnicoService _tecnicoService;

    public IndexModel(ITecnicoService tecnicoService)
    {
        _tecnicoService = tecnicoService;
    }

    public List<Tecnico> Tecnicos { get; set; } = [];

    [TempData]
    public string? MensagemSucesso { get; set; }

    [TempData]
    public string? MensagemErro { get; set; }

    [TempData]
    public string? CodigoAtivacaoGerado { get; set; }

    [TempData]
    public string? UsuarioCodigoGerado { get; set; }

    public async Task OnGetAsync()
    {
        Tecnicos = await _tecnicoService.ListarAsync();
    }

    public async Task<IActionResult> OnPostDesativarAsync(int id)
    {
        var ator = User.Identity?.Name ?? "sistema";
        try
        {
            var tecnico = await _tecnicoService.DesativarTecnicoAsync(id, ator);
            MensagemSucesso = $"Técnico '{tecnico.UsuarioDominio}' desativado com sucesso. Sessões ativas foram encerradas.";
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAtivarAsync(int id)
    {
        var ator = User.Identity?.Name ?? "sistema";
        try
        {
            var tecnico = await _tecnicoService.AtivarTecnicoAsync(id, ator);
            MensagemSucesso = $"Técnico '{tecnico.UsuarioDominio}' reativado com sucesso.";
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRedefinir2FaAsync(int id)
    {
        var ator = User.Identity?.Name ?? "sistema";
        try
        {
            var (tecnico, novoCodigo) = await _tecnicoService.Redefinir2FaAsync(id, ator);
            MensagemSucesso = $"2FA do técnico '{tecnico.UsuarioDominio}' redefinido com sucesso.";
            CodigoAtivacaoGerado = novoCodigo;
            UsuarioCodigoGerado = tecnico.UsuarioDominio;
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
        }

        return RedirectToPage();
    }
}
