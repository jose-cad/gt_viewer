using System.Security.Claims;

namespace GtViewer.Web.Security;

public static class ClaimsPrincipalExtensions
{
    public static bool HasPermissao(this ClaimsPrincipal? user, string permissao)
    {
        if (user == null || user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var permissoesClaim = user.FindFirst("Permissoes")?.Value;
        if (string.IsNullOrWhiteSpace(permissoesClaim))
        {
            return false;
        }

        var parts = permissoesClaim.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Contains(permissao, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAdmin(this ClaimsPrincipal? user)
    {
        return user.HasPermissao(Permissoes.AdministrarTecnicos);
    }
}
