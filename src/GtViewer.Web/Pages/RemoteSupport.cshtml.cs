using GtViewer.Web.Data;
using GtViewer.Web.Models;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Pages;

[Authorize(Policy = Permissoes.SuporteRemoto)]
public class RemoteSupportModel : PageModel
{
    private readonly ISuporteRemotoService? _suporteRemotoService;
    private readonly IMeshCentralClient _meshClient;
    private readonly GtViewerDbContext? _dbContext;
    private readonly MeshLoginTokenService? _tokenService;

    [ActivatorUtilitiesConstructor]
    public RemoteSupportModel(
        ISuporteRemotoService suporteRemotoService,
        IMeshCentralClient meshClient,
        GtViewerDbContext dbContext,
        MeshLoginTokenService tokenService)
    {
        _suporteRemotoService = suporteRemotoService;
        _meshClient = meshClient;
        _dbContext = dbContext;
        _tokenService = tokenService;
    }

    // Construtor de compatibilidade para PoC e testes unitários diretos
    public RemoteSupportModel(MeshLoginTokenService tokenService, IMeshCentralClient meshClient)
    {
        _tokenService = tokenService;
        _meshClient = meshClient;
    }

    [BindProperty(SupportsGet = true)]
    public int? MaquinaId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? NodeId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TechnicianUsername { get; set; }

    public string? SessionId { get; private set; }
    public string? RemoteDesktopUrl { get; private set; }
    public string? LoginToken { get; private set; }
    public string? MirrorUsername { get; private set; }
    public string? UsuarioAlvo { get; private set; }
    public string? MachineHostname { get; private set; }
    public long RightsMask => MeshRights.MinimalRemoteDesktopRights;
    public bool IsMeshConnected => _meshClient.IsConnected;
    public string? ErrorMessage { get; private set; }

    [NonHandler]
    public IActionResult OnGet() => OnGetAsync().GetAwaiter().GetResult();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!_meshClient.IsConnected)
        {
            ErrorMessage = "Sem conexão com o servidor de máquinas (MeshCentral) — acesso à tela remota recusado.";
            return Page();
        }

        var username = !string.IsNullOrWhiteSpace(TechnicianUsername)
            ? TechnicianUsername
            : User?.Identity?.Name ?? "tecnico";

        MirrorUsername = username.StartsWith("gtv_mirror_") ? username : $"gtv_mirror_{username}";

        // Modo isolado / standalone (sem banco) para testes de contrato PoC
        if (_dbContext == null || _suporteRemotoService == null)
        {
            if (string.IsNullOrWhiteSpace(NodeId))
            {
                NodeId = "node//demo_machine_01";
            }

            LoginToken = _tokenService?.GenerateLoginToken(MirrorUsername, expireMinutes: 2);
            RemoteDesktopUrl = _tokenService?.BuildRemoteDesktopUrl(LoginToken ?? "", NodeId, viewmode: 11, hide: 31);
            return Page();
        }

        var query = _dbContext.Maquinas.AsNoTracking();
        var maquina = MaquinaId.HasValue
            ? await query.FirstOrDefaultAsync(m => m.Id == MaquinaId.Value)
            : !string.IsNullOrWhiteSpace(NodeId)
                ? await query.FirstOrDefaultAsync(m => m.NodeId == NodeId)
                : null;

        if (maquina == null)
        {
            if (!string.IsNullOrWhiteSpace(NodeId))
            {
                LoginToken = _tokenService?.GenerateLoginToken(MirrorUsername, expireMinutes: 2);
                RemoteDesktopUrl = _tokenService?.BuildRemoteDesktopUrl(LoginToken ?? "", NodeId, viewmode: 11, hide: 31);
                return Page();
            }

            ErrorMessage = "Máquina não especificada ou não encontrada no inventário.";
            return Page();
        }

        MachineHostname = maquina.Hostname ?? maquina.NumeroSerie;
        NodeId = maquina.NodeId;
        MaquinaId = maquina.Id;

        try
        {
            var resultado = await _suporteRemotoService.IniciarSessaoAsync(maquina.Id, username);

            SessionId = resultado.SessionId;
            RemoteDesktopUrl = resultado.RemoteDesktopUrl;
            LoginToken = resultado.LoginToken;
            MirrorUsername = resultado.MirrorUsername;
            UsuarioAlvo = resultado.UsuarioAlvo;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostHeartbeatAsync([FromQuery] string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || _suporteRemotoService == null)
        {
            return BadRequest(new { ok = false, message = "SessionId obrigatório." });
        }

        var sucesso = await _suporteRemotoService.RenovarHeartbeatAsync(sessionId);
        if (!sucesso)
        {
            return StatusCode(StatusCodes.Status410Gone, new { ok = false, message = "Sessão expirada ou encerrada." });
        }

        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostEncerrarAsync(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _suporteRemotoService != null)
        {
            await _suporteRemotoService.EncerrarSessaoAsync(sessionId, "Sessão encerrada voluntariamente pelo técnico");
        }

        return RedirectToPage("/Maquinas/Index");
    }

    public async Task<IActionResult> OnPostRecusaAsync(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _suporteRemotoService != null)
        {
            await _suporteRemotoService.RegistrarRecusaAsync(sessionId, "Usuário local recusou o acesso");
        }

        return RedirectToPage("/Maquinas/Index");
    }
}
