using GtViewer.Web.Models.Inventario;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Maquinas;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IInventarioService _inventarioService;

    public IndexModel(IInventarioService inventarioService)
    {
        _inventarioService = inventarioService;
    }

    [BindProperty(SupportsGet = true)]
    public string? Busca { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Conexao { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Bloqueio { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Contrato { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ApenasAprovacao { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ApenasSemSerie { get; set; }

    public IReadOnlyList<MaquinaListItemDto> Maquinas { get; private set; } = new List<MaquinaListItemDto>();

    public int TotalMaquinas => Maquinas.Count;
    public int TotalOnline => Maquinas.Count(m => m.ConexaoEstado == "online");
    public int TotalBloqueadas => Maquinas.Count(m => m.BloqueioEstado == "bloqueada" || m.BloqueioEstado == "bloqueio_pendente");
    public int TotalEmAprovacao => Maquinas.Count(m => m.EmAprovacao);

    public async Task OnGetAsync()
    {
        var filter = new MaquinaFilterDto
        {
            Busca = Busca,
            ConexaoEstado = Conexao,
            BloqueioEstado = Bloqueio,
            SituacaoContrato = Contrato,
            ApenasEmAprovacao = ApenasAprovacao,
            ApenasSemSerie = ApenasSemSerie
        };

        Maquinas = await _inventarioService.ListMaquinasAsync(filter);
    }
}
