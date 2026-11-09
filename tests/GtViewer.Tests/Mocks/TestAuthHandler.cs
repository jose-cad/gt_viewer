using System.Security.Claims;
using System.Text.Encodings.Web;
using GtViewer.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GtViewer.Tests.Mocks;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestAuth";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var username = Request.Headers["X-Test-User"].FirstOrDefault();
        if (string.IsNullOrEmpty(username))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var permissionsHeader = Request.Headers["X-Test-Permissions"].FirstOrDefault() ?? Permissoes.SuporteRemoto;
        var permissions = permissionsHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.NameIdentifier, "99999"),
            new("IsAdmin", permissions.Contains(Permissoes.AdministrarTecnicos) ? "true" : "false")
        };

        foreach (var p in permissions)
        {
            claims.Add(new Claim("permissoes", p));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
