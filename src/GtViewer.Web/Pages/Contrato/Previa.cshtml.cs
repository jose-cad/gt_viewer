using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Pages.Contrato;

[Authorize]
public class PreviaModel : PageModel
{
    private readonly IPlanilhaLocadoraService _planilhaService;
    private readonly GtViewerDbContext _db;

    public PreviaModel(
        IPlanilhaLocadoraService planilhaService,
        GtViewerDbContext db)
    {
        _planilhaService = planilhaService;
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public bool ConfirmacaoReforcada { get; set; }

    public ContratoImportacao Importacao { get; set; } = null!;
    public List<string> AmostraSeriais { get; set; } = new();
    public bool ExigeConfirmacaoReforcada { get; set; }
    public double QuedaPercentual { get; set; }
    public int SeriesVigenteAnterior { get; set; }

    public string? MensagemErro { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!User.HasPermissao(Permissoes.ImportarPlanilha))
        {
            return Forbid();
        }

        var imp = await _db.ContratosImportacao
            .Include(c => c.Seriais)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (imp == null)
        {
            return NotFound();
        }

        Importacao = imp;
        AmostraSeriais = imp.Seriais.Take(15).Select(s => s.NumeroSerie).ToList();

        // Verifica queda em relação ao vigente
        var contratoVigente = await _db.ContratosImportacao
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Vigente && c.Id != id);

        if (contratoVigente != null && contratoVigente.SeriesValidas > 0)
        {
            SeriesVigenteAnterior = contratoVigente.SeriesValidas;
            if (imp.SeriesValidas < contratoVigente.SeriesValidas)
            {
                var diferenca = contratoVigente.SeriesValidas - imp.SeriesValidas;
                var queda = (double)diferenca / contratoVigente.SeriesValidas;
                if (queda > 0.20)
                {
                    ExigeConfirmacaoReforcada = true;
                    QuedaPercentual = queda * 100.0;
                }
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostConfirmarAsync()
    {
        if (!User.HasPermissao(Permissoes.ImportarPlanilha))
        {
            return Forbid();
        }

        try
        {
            var ator = User.Identity?.Name ?? "desconhecido";
            var confirmado = await _planilhaService.ConfirmarImportacaoAsync(
                Id,
                ator,
                confirmacaoReforcadaAceita: ConfirmacaoReforcada);

            TempData["MensagemSucesso"] = $"Contrato da planilha '{confirmado.NomeArquivo}' confirmado com sucesso como vigente ({confirmado.SeriesValidas} séries).";
            return RedirectToPage("/Contrato/Index");
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
            return await OnGetAsync(Id);
        }
    }
}
