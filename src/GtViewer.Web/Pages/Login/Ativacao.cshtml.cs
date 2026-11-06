using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GtViewer.Web.Pages.Login;

public class AtivacaoModel : PageModel
{
    private readonly IAuthService _authService;

    public AtivacaoModel(IAuthService authService)
    {
        _authService = authService;
    }

    [BindProperty(SupportsGet = true)]
    public int TecnicoId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string CodigoAtivacao { get; set; } = string.Empty;

    [BindProperty]
    public string PlainSecret { get; set; } = string.Empty;

    [BindProperty]
    public string TotpCode { get; set; } = string.Empty;

    public string? QrCodeSvg { get; set; }
    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (TecnicoId <= 0)
        {
            return RedirectToPage("/Login");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostValidarCodigoAsync()
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        var result = await _authService.ValidateActivationCodeAndBegin2FaSetupAsync(TecnicoId, CodigoAtivacao, clientIp);
        if (!result.Success)
        {
            ErrorMessage = result.Error ?? "Código de ativação inválido.";
            return Page();
        }

        PlainSecret = result.PlainSecret ?? string.Empty;
        QrCodeSvg = result.QrCodeSvg;
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmar2FaAsync()
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        var result = await _authService.Confirm2FaSetupAsync(TecnicoId, PlainSecret, TotpCode, clientIp);
        if (!result.Success)
        {
            ErrorMessage = result.Error ?? "Código incorreto.";
            return Page();
        }

        if (result.Principal != null)
        {
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) // Sessão de 8h conforme especificação
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                result.Principal,
                authProperties);
        }

        return LocalRedirect(ReturnUrl ?? "/");
    }
}
