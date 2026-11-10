using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Pages.Tarefas;

[Authorize]
public class DetalhesModel : PageModel
{
    private readonly GtViewerDbContext _db;

    public DetalhesModel(GtViewerDbContext db)
    {
        _db = db;
    }

    public Tarefa? Tarefa { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Tarefa = await _db.Tarefas
            .Include(t => t.AppCatalogo)
            .Include(t => t.Grupo)
            .Include(t => t.Acoes)
                .ThenInclude(a => a.Maquina)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (Tarefa == null)
        {
            return NotFound();
        }

        return Page();
    }
}
