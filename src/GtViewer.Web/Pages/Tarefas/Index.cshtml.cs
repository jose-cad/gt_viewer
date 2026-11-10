using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Pages.Tarefas;

[Authorize]
public class IndexModel : PageModel
{
    private readonly GtViewerDbContext _db;

    public IndexModel(GtViewerDbContext db)
    {
        _db = db;
    }

    public List<TarefaItemViewModel> Tarefas { get; set; } = new();

    public async Task OnGetAsync()
    {
        var tarefas = await _db.Tarefas
            .Include(t => t.AppCatalogo)
            .Include(t => t.Grupo)
            .Include(t => t.Acoes)
            .OrderByDescending(t => t.CriadoEm)
            .Take(50)
            .ToListAsync();

        Tarefas = tarefas.Select(t => new TarefaItemViewModel
        {
            Id = t.Id,
            Tipo = t.Tipo,
            AppNome = t.AppCatalogo?.Nome ?? "-",
            AppVersao = t.AppCatalogo?.Versao ?? "-",
            GrupoNome = t.Grupo?.Nome,
            Ator = t.Ator,
            CriadoEm = t.CriadoEm,
            ConcluidoEm = t.ConcluidoEm,
            TotalAcoes = t.Acoes.Count,
            Pendentes = t.Acoes.Count(a => a.Estado == "pendente" || a.Estado == "enviada" || a.Estado == "resultado_desconhecido"),
            Sucesso = t.Acoes.Count(a => a.Estado == "executada"),
            Falhas = t.Acoes.Count(a => a.Estado == "falhou"),
            Canceladas = t.Acoes.Count(a => a.Estado == "cancelada")
        }).ToList();
    }
}

public class TarefaItemViewModel
{
    public int Id { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public string AppNome { get; set; } = string.Empty;
    public string AppVersao { get; set; } = string.Empty;
    public string? GrupoNome { get; set; }
    public string Ator { get; set; } = string.Empty;
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset? ConcluidoEm { get; set; }
    public int TotalAcoes { get; set; }
    public int Pendentes { get; set; }
    public int Sucesso { get; set; }
    public int Falhas { get; set; }
    public int Canceladas { get; set; }
}
