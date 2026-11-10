using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Contrato;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IPlanilhaLocadoraService _planilhaService;

    public IndexModel(IPlanilhaLocadoraService planilhaService)
    {
        _planilhaService = planilhaService;
    }

    public RelatorioConferenciaResult Relatorio { get; set; } = null!;
    public List<ContratoImportacao> Historico { get; set; } = new();

    [TempData]
    public string? MensagemSucesso { get; set; }

    [TempData]
    public string? MensagemErro { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Relatorio = await _planilhaService.ObterRelatorioConferenciaAsync();
        Historico = await _planilhaService.ListarHistoricoImportacoesAsync();
        return Page();
    }
}
