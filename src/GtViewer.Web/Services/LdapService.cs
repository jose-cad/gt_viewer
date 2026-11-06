using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;

namespace GtViewer.Web.Services;

public class LdapService : ILdapService
{
    private readonly LdapOptions _options;
    private readonly ILogger<LdapService> _logger;

    public LdapService(IOptions<LdapOptions> options, ILogger<LdapService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public static string EscapeLdapFilter(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder();
        foreach (var c in input)
        {
            switch (c)
            {
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\\': sb.Append(@"\5c"); break;
                case '\0': sb.Append(@"\00"); break;
                case '/': sb.Append(@"\2f"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private LdapConnection CreateConnection()
    {
        var identifier = new LdapDirectoryIdentifier(_options.Server, _options.Port, fullyQualifiedDnsHostName: true, connectionless: false);
        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;

        if (_options.ValidateServerCertificate)
        {
            connection.SessionOptions.VerifyServerCertificate = (conn, cert) =>
            {
                if (cert == null)
                {
                    _logger.LogWarning("Certificado LDAPS ausente ou nulo.");
                    return false;
                }

                try
                {
                    using var cert2 = new X509Certificate2(cert);
                    using var chain = new X509Chain();
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                    var isValid = chain.Build(cert2);
                    if (!isValid)
                    {
                        _logger.LogWarning("Certificado LDAPS falhou na validação de cadeia de confiança.");
                    }
                    return isValid;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao validar cadeia de certificado LDAPS.");
                    return false;
                }
            };
        }

        return connection;
    }

    public Task<LdapAuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(new LdapAuthResult(false, ErrorMessage: "Usuário ou senha vazios."));
        }

        var escapedUser = EscapeLdapFilter(username.Trim());
        var userPrincipal = escapedUser.Contains('@') ? escapedUser : $"{escapedUser}@{_options.Domain}";

        try
        {
            using var connection = CreateConnection();
            connection.Bind(new NetworkCredential(userPrincipal, password));

            // Verificar se o usuário está ativo no AD via busca
            var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={escapedUser}))";
            var searchRequest = new SearchRequest(
                _options.BaseDn,
                filter,
                SearchScope.Subtree,
                "displayName", "mail", "userAccountControl");

            var response = (SearchResponse)connection.SendRequest(searchRequest);
            if (response.Entries.Count == 0)
            {
                // Usuário autenticou via bind mas não foi encontrado no BaseDn de busca
                return Task.FromResult(new LdapAuthResult(true, DisplayName: username));
            }

            var entry = response.Entries[0];
            var uacVal = entry.Attributes["userAccountControl"]?[0]?.ToString();
            if (int.TryParse(uacVal, out var uac))
            {
                const int accountDisabledFlag = 2;
                if ((uac & accountDisabledFlag) != 0)
                {
                    _logger.LogWarning("Usuário '{User}' autenticado no AD, porém conta está desativada (UAC={Uac}).", username, uac);
                    return Task.FromResult(new LdapAuthResult(false, ErrorMessage: "Conta desativada no domínio."));
                }
            }

            var displayName = entry.Attributes["displayName"]?[0]?.ToString() ?? username;
            var email = entry.Attributes["mail"]?[0]?.ToString();

            return Task.FromResult(new LdapAuthResult(true, DisplayName: displayName, Email: email));
        }
        catch (LdapException ex) when (ex.ErrorCode == 49 || ex.Message.Contains("AcceptSecurityContext") || ex.Message.Contains("data 52e"))
        {
            // Erro 49 = Invalid Credentials
            _logger.LogInformation("Credenciais inválidas no AD para o usuário '{User}'.", username);
            return Task.FromResult(new LdapAuthResult(false, ErrorMessage: "Credenciais inválidas no AD."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro de conexão/infraestrutura ao autenticar usuário '{User}' no AD.", username);
            return Task.FromResult(new LdapAuthResult(false, IsConnectionError: true, ErrorMessage: "Falha de conexão com o AD."));
        }
    }

    public Task<LdapUserStatus> CheckUserStatusAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.FromResult(new LdapUserStatus(false, false));
        }

        var escapedUser = EscapeLdapFilter(username.Trim());

        try
        {
            using var connection = CreateConnection();
            connection.Bind(); // Anonymous ou com conta de serviço se configurado

            var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={escapedUser}))";
            var searchRequest = new SearchRequest(
                _options.BaseDn,
                filter,
                SearchScope.Subtree,
                "userAccountControl");

            var response = (SearchResponse)connection.SendRequest(searchRequest);
            if (response.Entries.Count == 0)
            {
                return Task.FromResult(new LdapUserStatus(false, false));
            }

            var entry = response.Entries[0];
            var uacVal = entry.Attributes["userAccountControl"]?[0]?.ToString();
            var isActive = true;
            if (int.TryParse(uacVal, out var uac))
            {
                const int accountDisabledFlag = 2;
                if ((uac & accountDisabledFlag) != 0)
                {
                    isActive = false;
                }
            }

            return Task.FromResult(new LdapUserStatus(isActive, true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro de rede/conexão ao checar status do usuário '{User}' no AD.", username);
            return Task.FromResult(new LdapUserStatus(false, false, IsConnectionError: true));
        }
    }
}
