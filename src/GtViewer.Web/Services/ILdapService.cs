namespace GtViewer.Web.Services;

public class LdapOptions
{
    public const string SectionName = "Ldap";

    public string Server { get; set; } = "ad.localhost";
    public int Port { get; set; } = 636;
    public string Domain { get; set; } = "empresa.local";
    public string BaseDn { get; set; } = "DC=empresa,DC=local";
    public bool ValidateServerCertificate { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public record LdapAuthResult(
    bool Success,
    bool IsConnectionError = false,
    string? ErrorMessage = null,
    string? DisplayName = null,
    string? Email = null);

public record LdapUserStatus(
    bool IsActive,
    bool Exists,
    bool IsConnectionError = false);

public interface ILdapService
{
    Task<LdapAuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<LdapUserStatus> CheckUserStatusAsync(string username, CancellationToken cancellationToken = default);
}
