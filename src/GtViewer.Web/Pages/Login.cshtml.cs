using GtViewer.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages;

public class LoginModel : PageModel
{
    private readonly IAuthService _authService;

    public LoginModel(IAuthService authService)
    {
        _authService = authService;
    }

    [BindProperty]
    public string Usuario { get; set; } = string.Empty;

    [BindProperty]
    public string Senha { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ReturnUrl ?? "/");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        var result = await _authService.AuthenticateInitialAsync(Usuario, Senha, clientIp);
        if (!result.Success)
        {
            ErrorMessage = result.Error ?? "Credenciais inválidas.";
            return Page();
        }

        if (result.RequiresActivation)
        {
            return RedirectToPage("/Login/Ativacao", new { tecnicoId = result.TecnicoId, returnUrl = ReturnUrl });
        }

        if (result.Requires2Fa)
        {
            return RedirectToPage("/Login/Verificar2Fa", new { tecnicoId = result.TecnicoId, returnUrl = ReturnUrl });
        }

        return LocalRedirect(ReturnUrl ?? "/");
    }
}
