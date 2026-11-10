using GtViewer.Web.Models;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Reflection;

namespace GtViewer.Web.Pages.Auditoria;

[Authorize(Policy = Permissoes.VerAuditoria)]
public class IndexModel : PageModel
{
    private readonly IAuditoriaService _auditoriaService;

    public IndexModel(IAuditoriaService auditoriaService)
    {
        _auditoriaService = auditoriaService;
    }

    [BindProperty(SupportsGet = true)]
    public string? Ator { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Maquina { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tipo { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DataInicio { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? DataFim { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool? Sucesso { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? EstadoAcao { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Pagina { get; set; } = 1;

    public AuditoriaPagedResult Resultado { get; set; } = new();
    public List<string> AtoresDisponiveis { get; set; } = new();
    public List<string> TiposDisponiveis { get; set; } = new();
    public List<string> EstadosAcaoDisponiveis { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!User.HasPermissao(Permissoes.VerAuditoria))
        {
            return Forbid();
        }

        CarregarListasAuxiliares();
        AtoresDisponiveis = await _auditoriaService.ObterAtoresDistintosAsync();

        var filtro = new AuditoriaFiltro
        {
            Ator = Ator,
            Maquina = Maquina,
            Tipo = Tipo,
            DataInicio = DataInicio.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(DataInicio.Value, DateTimeKind.Utc)) : null,
            DataFim = DataFim.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(DataFim.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc)) : null,
            Sucesso = Sucesso,
            EstadoAcao = EstadoAcao,
            Pagina = Pagina,
            TamanhoPagina = 50
        };

        Resultado = await _auditoriaService.ConsultarAsync(filtro);
        return Page();
    }

    public async Task<IActionResult> OnGetExportarAsync()
    {
        if (!User.HasPermissao(Permissoes.VerAuditoria))
        {
            return Forbid();
        }

        var filtro = new AuditoriaFiltro
        {
            Ator = Ator,
            Maquina = Maquina,
            Tipo = Tipo,
            DataInicio = DataInicio.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(DataInicio.Value, DateTimeKind.Utc)) : null,
            DataFim = DataFim.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(DataFim.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc)) : null,
            Sucesso = Sucesso,
            EstadoAcao = EstadoAcao,
            Pagina = 1,
            TamanhoPagina = 5000
        };

        var bytes = await _auditoriaService.ExportarExcelAsync(filtro);
        var filename = $"Auditoria_GtViewer_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    private void CarregarListasAuxiliares()
    {
        // Obtém todos os tipos fechados de TiposAuditoria
        TiposDisponiveis = typeof(TiposAuditoria)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .OrderBy(t => t)
            .ToList();

        // Estados de ação possíveis
        EstadosAcaoDisponiveis = new List<string>
        {
            EstadosAcao.Pendente,
            EstadosAcao.Enviada,
            EstadosAcao.Executada,
            EstadosAcao.Falhou,
            EstadosAcao.ResultadoDesconhecido,
            EstadosAcao.Cancelada
        };
    }
}
