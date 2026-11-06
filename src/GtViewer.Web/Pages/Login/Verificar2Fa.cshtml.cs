using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Login;

public class Verificar2FaModel : PageModel
{
    private readonly IAuthService _authService;

    public Verificar2FaModel(IAuthService authService)
    {
        _authService = authService;
    }

    [BindProperty(SupportsGet = true)]
    public int TecnicoId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string TotpCode { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (TecnicoId <= 0)
        {
            return RedirectToPage("/Login");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        var result = await _authService.Validate2FaLoginAsync(TecnicoId, TotpCode, clientIp);
        if (!result.Success)
        {
            ErrorMessage = result.Error ?? "Código 2FA inválido.";
            return Page();
        }

        if (result.Principal != null)
        {
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) // Sessão absoluta de 8 horas (J1 passo 5)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                result.Principal,
                authProperties);
        }

        return LocalRedirect(ReturnUrl ?? "/");
    }
}
