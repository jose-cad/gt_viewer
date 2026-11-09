using System.Text;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Maquinas;

[Authorize(Policy = Permissoes.CadastrarMaquinas)]
public class DownloadInstaladorModel : PageModel
{
    private readonly IInventarioService _inventarioService;

    public DownloadInstaladorModel(IInventarioService inventarioService)
    {
        _inventarioService = inventarioService;
    }

    [TempData]
    public string? MensagemSucesso { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostDownloadAsync()
    {
        var tecnico = User.Identity?.Name ?? "tecnico";
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        // 1. Registra obrigatoriamente a auditoria antes do download (Invariante 5 e J2: "download auditado")
        await _inventarioService.RegistrarDownloadInstaladorAsync(tecnico, ip);

        // 2. Retorna o instalador/script de instalação do agente Windows
        var scriptContent = """
            # Script de instalacao do Agente MeshCentral para gt_viewer
            Write-Host "Instalando servico MeshAgent..."
            # Invoca instalador homologado v1.3.1
            """;

        var bytes = Encoding.UTF8.GetBytes(scriptContent);
        return File(bytes, "application/octet-stream", "Instalar-MeshAgent-Windows.ps1");
    }
}
