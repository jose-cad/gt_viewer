using GtViewer.Web.Models.Inventario;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Maquinas;

[Authorize(Policy = Permissoes.CadastrarMaquinas)]
public class AprovacaoModel : PageModel
{
    private readonly IInventarioService _inventarioService;

    public AprovacaoModel(IInventarioService inventarioService)
    {
        _inventarioService = inventarioService;
    }

    public IReadOnlyList<MaquinaListItemDto> MaquinasAprovacao { get; private set; } = new List<MaquinaListItemDto>();
    public IReadOnlyList<MaquinaListItemDto> MaquinasSemSerie { get; private set; } = new List<MaquinaListItemDto>();

    [TempData]
    public string? MensagemSucesso { get; set; }

    [TempData]
    public string? MensagemErro { get; set; }

    public async Task OnGetAsync()
    {
        MaquinasAprovacao = await _inventarioService.ListMaquinasAsync(new MaquinaFilterDto { ApenasEmAprovacao = true });
        MaquinasSemSerie = await _inventarioService.ListMaquinasAsync(new MaquinaFilterDto { ApenasSemSerie = true });
    }

    public async Task<IActionResult> OnPostAprovarAsync(int maquinaId, bool manterBloqueio, string? justificativa)
    {
        var tecnico = User.Identity?.Name ?? "sistema";
        var podeBloquear = User.HasPermissao(Permissoes.Bloquear);

        var result = await _inventarioService.AprovarTrocaAgenteAsync(
            maquinaId,
            tecnico,
            manterBloqueio,
            justificativa,
            podeBloquear);

        if (result.Sucesso)
        {
            MensagemSucesso = "Troca de agente aprovada com sucesso! Uma nova conta de resgate foi agendada.";
        }
        else
        {
            MensagemErro = result.MensagemErro ?? "Falha ao aprovar troca de agente.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejeitarAsync(int maquinaId)
    {
        var tecnico = User.Identity?.Name ?? "sistema";
        var ok = await _inventarioService.RejeitarTrocaAgenteAsync(maquinaId, tecnico);
        if (ok)
        {
            MensagemSucesso = "Agente candidato rejeitado com sucesso.";
        }
        else
        {
            MensagemErro = "Falha ao rejeitar candidato.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCorrigirSerieAsync(int maquinaId, string novaSerie)
    {
        var tecnico = User.Identity?.Name ?? "sistema";
        if (string.IsNullOrWhiteSpace(novaSerie))
        {
            MensagemErro = "O número de série não pode estar vazio.";
            return RedirectToPage();
        }

        var ok = await _inventarioService.CorrigirSerieManualAsync(maquinaId, novaSerie, tecnico);
        if (ok)
        {
            MensagemSucesso = $"Série corrigida manualmente para '{novaSerie.Trim().ToUpperInvariant()}' com sucesso.";
        }
        else
        {
            MensagemErro = "Falha ao corrigir número de série.";
        }

        return RedirectToPage();
    }
}
