using GtViewer.Web.Data;
using GtViewer.Web.Models.Inventario;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Pages.Maquinas;

[Authorize]
public class DetalhesModel : PageModel
{
    private readonly IInventarioService _inventarioService;
    private readonly IBloqueioService _bloqueioService;
    private readonly IMeshCentralClient _meshClient;
    private readonly GtViewerDbContext _dbContext;

    public DetalhesModel(
        IInventarioService inventarioService,
        IBloqueioService bloqueioService,
        IMeshCentralClient meshClient,
        GtViewerDbContext dbContext)
    {
        _inventarioService = inventarioService;
        _bloqueioService = bloqueioService;
        _meshClient = meshClient;
        _dbContext = dbContext;
    }

    public MaquinaDetailDto? Maquina { get; private set; }

    public bool PodeBloquear => User.HasPermissao(Permissoes.Bloquear);
    public bool PodeRevelarSenha => User.HasPermissao(Permissoes.RevelarSenhaResgate);
    public bool MeshCentralConectado => _meshClient.IsConnected;

    public bool PodeRevelarSenhaAgora { get; private set; }
    public string? MotivoIndisponivelRevelar { get; private set; }

    [TempData]
    public string? MensagemSucesso { get; set; }

    [TempData]
    public string? MensagemErro { get; set; }

    public string? SenhaRevelada { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        await CarregarDadosAsync(id);
        if (Maquina == null)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostBloquearAsync(int id, string motivo)
    {
        if (!PodeBloquear)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(motivo))
        {
            MensagemErro = "O motivo do bloqueio é obrigatório.";
            return RedirectToPage(new { id });
        }

        try
        {
            await _bloqueioService.SolicitarBloqueioAsync(id, User.Identity?.Name ?? "tecnico", motivo);
            MensagemSucesso = "Bloqueio solicitado com sucesso. Será aplicado na máquina.";
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDesbloquearAsync(int id)
    {
        if (!PodeBloquear)
        {
            return Forbid();
        }

        try
        {
            await _bloqueioService.SolicitarDesbloqueioAsync(id, User.Identity?.Name ?? "tecnico");
            MensagemSucesso = "Desbloqueio solicitado com sucesso.";
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRevelarSenhaAsync(int id, string totpCode)
    {
        if (!PodeRevelarSenha)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(totpCode) || totpCode.Trim().Length != 6)
        {
            MensagemErro = "Informe o código de 6 dígitos do autenticador.";
            await CarregarDadosAsync(id);
            return Page();
        }

        try
        {
            SenhaRevelada = await _bloqueioService.RevelarSenhaResgateAsync(
                id,
                User.Identity?.Name ?? "tecnico",
                totpCode.Trim());

            MensagemSucesso = "Senha de resgate revelada com sucesso. A senha será trocada automaticamente na próxima conexão da máquina.";
        }
        catch (Exception ex)
        {
            MensagemErro = ex.Message;
        }

        await CarregarDadosAsync(id);
        return Page();
    }

    private async Task CarregarDadosAsync(int id)
    {
        Maquina = await _inventarioService.GetMaquinaDetailAsync(id);
        if (Maquina != null)
        {
            var maquinaEntity = await _dbContext.Maquinas
                .Include(m => m.Acoes)
                .FirstOrDefaultAsync(m => m.Id == id);

            if (maquinaEntity != null)
            {
                PodeRevelarSenhaAgora = _bloqueioService.PodeRevelarSenha(maquinaEntity, out var motivo);
                MotivoIndisponivelRevelar = motivo;
            }
        }
    }
}
