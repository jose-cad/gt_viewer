using System.ComponentModel.DataAnnotations;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Contrato;

[Authorize]
public class ImportarModel : PageModel
{
    private readonly IPlanilhaLocadoraService _planilhaService;

    public ImportarModel(IPlanilhaLocadoraService planilhaService)
    {
        _planilhaService = planilhaService;
    }

    [BindProperty]
    [Required(ErrorMessage = "Selecione o arquivo da planilha (.xlsx).")]
    public IFormFile? Arquivo { get; set; }

    public string? MensagemErro { get; set; }

    public IActionResult OnGet()
    {
        if (!User.HasPermissao(Permissoes.ImportarPlanilha))
        {
            return Forbid();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!User.HasPermissao(Permissoes.ImportarPlanilha))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Arquivo == null || Arquivo.Length == 0)
        {
            ModelState.AddModelError("Arquivo", "Selecione um arquivo válido.");
            return Page();
        }

        try
        {
            var ator = User.Identity?.Name ?? "desconhecido";
            await using var stream = Arquivo.OpenReadStream();
            var previa = await _planilhaService.ProcessarPreviaAsync(
                stream,
                Arquivo.FileName,
                Arquivo.Length,
                ator);

            return RedirectToPage("/Contrato/Previa", new { id = previa.ImportacaoId });
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
            return Page();
        }
    }
}
