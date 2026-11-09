using GtViewer.Web.Models.Inventario;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Maquinas;

[Authorize]
public class DetalhesModel : PageModel
{
    private readonly IInventarioService _inventarioService;

    public DetalhesModel(IInventarioService inventarioService)
    {
        _inventarioService = inventarioService;
    }

    public MaquinaDetailDto? Maquina { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Maquina = await _inventarioService.GetMaquinaDetailAsync(id);
        if (Maquina == null)
        {
            return NotFound();
        }

        return Page();
    }
}
