using GtViewer.Web.Models;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages;

[Authorize(Policy = Permissoes.SuporteRemoto)]
public class RemoteSupportModel : PageModel
{
    private readonly MeshLoginTokenService _tokenService;

    public RemoteSupportModel(MeshLoginTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [BindProperty(SupportsGet = true)]
    public string? NodeId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string TechnicianUsername { get; set; } = "tecnico_joao";

    public string MirrorUsername => $"gtv_mirror_{TechnicianUsername}";

    public string? RemoteDesktopUrl { get; private set; }
    public string? LoginToken { get; private set; }
    public long RightsMask => MeshRights.MinimalRemoteDesktopRights;

    public IActionResult OnGet()
    {
        if (string.IsNullOrWhiteSpace(NodeId))
        {
            // Se nenhum nodeId foi informado, exibe a página no modo de seleção/demonstração
            NodeId = "node//demo_machine_01";
        }

        // Gera token temporário de 2 minutos para o usuário espelho
        LoginToken = _tokenService.GenerateLoginToken(MirrorUsername, expireMinutes: 2);

        // Constrói URL com viewmode=11 (canvas direto de Desktop) e hide=31 (sem barras de navegação)
        RemoteDesktopUrl = _tokenService.BuildRemoteDesktopUrl(LoginToken, NodeId, viewmode: 11, hide: 31);

        return Page();
    }
}
