using GtViewer.Web.Services;

namespace GtViewer.Tests.Mocks;

public class MockLdapService : ILdapService
{
    public int AuthenticateCalls { get; private set; }
    public int CheckStatusCalls { get; private set; }

    public Func<string, string, LdapAuthResult>? AuthenticateHandler { get; set; }
    public Func<string, LdapUserStatus>? CheckStatusHandler { get; set; }

    public Task<LdapAuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        AuthenticateCalls++;
        if (AuthenticateHandler != null)
        {
            return Task.FromResult(AuthenticateHandler(username, password));
        }

        if (password == "SenhaCorreta123!")
        {
            return Task.FromResult(new LdapAuthResult(true, DisplayName: $"Técnico {username}"));
        }

        return Task.FromResult(new LdapAuthResult(false, ErrorMessage: "Senha incorreta"));
    }

    public Task<LdapUserStatus> CheckUserStatusAsync(string username, CancellationToken cancellationToken = default)
    {
        CheckStatusCalls++;
        if (CheckStatusHandler != null)
        {
            return Task.FromResult(CheckStatusHandler(username));
        }

        return Task.FromResult(new LdapUserStatus(IsActive: true, Exists: true));
    }
}
