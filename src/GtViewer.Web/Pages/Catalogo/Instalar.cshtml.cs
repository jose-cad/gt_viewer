using System.ComponentModel.DataAnnotations;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Pages.Catalogo;

[Authorize]
public class InstalarModel : PageModel
{
    private readonly ICatalogoService _catalogoService;
    private readonly IFilaAcoesService _filaAcoesService;
    private readonly GtViewerDbContext _db;

    public InstalarModel(
        ICatalogoService catalogoService,
        IFilaAcoesService filaAcoesService,
        GtViewerDbContext db)
    {
        _catalogoService = catalogoService;
        _filaAcoesService = filaAcoesService;
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public int? AppId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MaquinaId { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Selecione o aplicativo do catálogo.")]
    public int SelectedAppId { get; set; }

    [BindProperty]
    [Required]
    public string Operacao { get; set; } = TiposAcao.Instalar; // "instalar" ou "remover"

    [BindProperty]
    [Required]
    public string ModoAlvo { get; set; } = "maquinas"; // "grupo" ou "maquinas"

    [BindProperty]
    public int? SelectedGrupoId { get; set; }

    [BindProperty]
    public List<int> SelectedMaquinaIds { get; set; } = new();

    public List<AppCatalogo> Apps { get; set; } = new();
    public List<Grupo> Grupos { get; set; } = new();
    public List<Maquina> Maquinas { get; set; } = new();

    public string? MensagemErro { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!User.HasPermissao(Permissoes.InstalarApps))
        {
            return Forbid();
        }

        await CarregarListasAsync();

        if (AppId.HasValue)
        {
            SelectedAppId = AppId.Value;
        }

        if (MaquinaId.HasValue)
        {
            SelectedMaquinaIds.Add(MaquinaId.Value);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!User.HasPermissao(Permissoes.InstalarApps))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            await CarregarListasAsync();
            return Page();
        }

        var app = await _catalogoService.ObterAppPorIdAsync(SelectedAppId);
        if (app == null || !app.Ativo)
        {
            MensagemErro = "Aplicativo inválido ou inativo no catálogo.";
            await CarregarListasAsync();
            return Page();
        }

        var ator = User.Identity?.Name ?? "desconhecido";
        var tipo = Operacao == TiposAcao.Remover ? TiposAcao.Remover : TiposAcao.Instalar;

        try
        {
            Tarefa tarefa;
            if (ModoAlvo == "grupo")
            {
                if (!SelectedGrupoId.HasValue)
                {
                    MensagemErro = "Selecione um grupo de máquinas.";
                    await CarregarListasAsync();
                    return Page();
                }

                tarefa = await _filaAcoesService.CriarTarefaGrupoAsync(
                    SelectedGrupoId.Value,
                    tipo,
                    ator,
                    appCatalogoId: app.Id);
            }
            else
            {
                if (SelectedMaquinaIds == null || SelectedMaquinaIds.Count == 0)
                {
                    MensagemErro = "Selecione pelo menos uma máquina alvo.";
                    await CarregarListasAsync();
                    return Page();
                }

                tarefa = await _filaAcoesService.CriarTarefaMaquinasAsync(
                    SelectedMaquinaIds,
                    tipo,
                    ator,
                    appCatalogoId: app.Id);
            }

            TempData["MensagemSucesso"] = $"Tarefa #{tarefa.Id} criada com sucesso para {tipo} do aplicativo '{app.Nome}'.";
            return RedirectToPage("/Tarefas/Detalhes", new { id = tarefa.Id });
        }
        catch (Exception ex)
        {
            MensagemErro = $"Erro ao enfileirar tarefa: {ex.Message}";
            await CarregarListasAsync();
            return Page();
        }
    }

    private async Task CarregarListasAsync()
    {
        Apps = await _catalogoService.ListarAppsAsync(apenasAtivos: true);
        Grupos = await _db.Grupos.AsNoTracking().OrderBy(g => g.Nome).ToListAsync();
        Maquinas = await _db.Maquinas.AsNoTracking()
            .Where(m => !m.EmAprovacao)
            .OrderBy(m => m.Hostname ?? m.NumeroSerie)
            .ToListAsync();
    }
}
