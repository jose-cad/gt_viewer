using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GtViewer.Web.Models;
using GtViewer.Web.Pages;
using GtViewer.Web.Services;
using Xunit;

namespace GtViewer.Tests;

public class RemoteDesktopPocTests
{
    private const string FixedHexKey = "9ae4fb0b32a70e6fecb808f07ed6259ecb5336327b9aef2ac2c46b9d0b0f10a3ddfa47147b4c6dfe484a2042e255b07def08ca68d26c4ad0bc782ead178700525f3b707ab9493c940ff40b9cddfdc3c0";

    [Fact]
    public void GenerateLoginToken_CreatesValidBase64TokenWithMeshCentralSubstitutions()
    {
        var service = new MeshLoginTokenService(FixedHexKey, "https://mesh.localhost");

        var token = service.GenerateLoginToken("gtv_mirror_joao", expireMinutes: 2);

        Assert.NotNull(token);
        Assert.NotEmpty(token);
        // MeshCentral requires '+' replaced by '@' and '/' replaced by '$'
        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
    }

    [Fact]
    public void DecryptLoginToken_RoundTrip_MatchesOriginalPayload()
    {
        var service = new MeshLoginTokenService(FixedHexKey, "https://mesh.localhost");
        var username = "gtv_mirror_maria";

        var token = service.GenerateLoginToken(username, expireMinutes: 2);
        var payload = service.DecryptLoginToken(token);

        Assert.NotNull(payload);
        Assert.Equal($"user//{username}", payload.User);
        Assert.Equal(3, payload.AuthType);
        Assert.Equal(2, payload.ExpireMinutes);
        Assert.True(payload.Time > 0);
    }

    [Fact]
    public void DecryptLoginToken_WithTamperedCiphertext_ReturnsNull()
    {
        var service = new MeshLoginTokenService(FixedHexKey, "https://mesh.localhost");
        var token = service.GenerateLoginToken("gtv_mirror_tecnico", expireMinutes: 2);

        // Adultera um caractere no meio do token
        var chars = token.ToCharArray();
        chars[15] = chars[15] == 'A' ? 'B' : 'A';
        var tamperedToken = new string(chars);

        var payload = service.DecryptLoginToken(tamperedToken);

        Assert.Null(payload);
    }

    [Fact]
    public void IsTokenValid_EvaluatesExpirationCorrectly()
    {
        var service = new MeshLoginTokenService(FixedHexKey, "https://mesh.localhost");
        var now = DateTimeOffset.UtcNow;

        var validPayload = new MeshLoginTokenPayload
        {
            User = "user//tecnico",
            AuthType = 3,
            ExpireMinutes = 2,
            Time = now.ToUnixTimeSeconds() - 60 // 1 minuto atrás (dentro da janela de 2 min)
        };
        Assert.True(service.IsTokenValid(validPayload, now));

        var expiredPayload = new MeshLoginTokenPayload
        {
            User = "user//tecnico",
            AuthType = 3,
            ExpireMinutes = 2,
            Time = now.ToUnixTimeSeconds() - 130 // 130s atrás (> 120s)
        };
        Assert.False(service.IsTokenValid(expiredPayload, now));

        var clockSkewFuturePayload = new MeshLoginTokenPayload
        {
            User = "user//tecnico",
            AuthType = 3,
            ExpireMinutes = 2,
            Time = now.ToUnixTimeSeconds() + 45 // 45s no futuro (> 30s de tolerância)
        };
        Assert.False(service.IsTokenValid(clockSkewFuturePayload, now));
    }

    [Fact]
    public void BuildRemoteDesktopUrl_GeneratesExpectedQueryParameters()
    {
        var service = new MeshLoginTokenService(FixedHexKey, "https://mesh.empresa.com.br");
        var token = "token_teste_123";
        var nodeId = "node//VM-TI-01";

        var url = service.BuildRemoteDesktopUrl(token, nodeId, viewmode: 11, hide: 31);

        Assert.StartsWith("https://mesh.empresa.com.br/?login=token_teste_123", url);
        Assert.Contains("gotonode=node%2F%2FVM-TI-01", url);
        Assert.Contains("viewmode=11", url);
        Assert.Contains("hide=31", url);
    }

    [Fact]
    public void MeshRights_MinimalRemoteDesktop_HasOnlyRemoteControlAndNoSensitiveBits()
    {
        var rights = MeshRights.MinimalRemoteDesktopRights;

        // Bitmask estrito 8 (MESHRIGHT_REMOTECONTROL)
        Assert.Equal(8, rights);

        // Permite visualização e controle de tela
        Assert.True(MeshRights.HasRemoteControl(rights));

        // Proíbe expressamente:
        Assert.False(MeshRights.HasTerminal(rights));       // Bit 16: Console de agente / Terminal
        Assert.False(MeshRights.HasFiles(rights));          // Bit 32: Servidor de arquivos
        Assert.False(MeshRights.HasCommands(rights));       // Bit 131072: Execução de comandos arbitrários
        Assert.False(MeshRights.HasManageUsers(rights));    // Bit 2: Gerenciar usuários
        Assert.False(MeshRights.HasManageComputers(rights));// Bit 4: Gerenciar computadores
        Assert.NotEqual(MeshRights.MESHRIGHT_ADMIN, rights);// Não é administrador
    }

    [Fact]
    public void RemoteSupportPageModel_InitializesCorrectly()
    {
        var service = new MeshLoginTokenService(FixedHexKey, "https://mesh.localhost");
        var pageModel = new RemoteSupportModel(service)
        {
            NodeId = "node//win11_dev",
            TechnicianUsername = "carlos"
        };

        var result = pageModel.OnGet();

        Assert.NotNull(result);
        Assert.Equal("gtv_mirror_carlos", pageModel.MirrorUsername);
        Assert.Equal(8, pageModel.RightsMask);
        Assert.NotNull(pageModel.LoginToken);
        Assert.NotNull(pageModel.RemoteDesktopUrl);
        Assert.Contains("viewmode=11", pageModel.RemoteDesktopUrl);
        Assert.Contains("hide=31", pageModel.RemoteDesktopUrl);
    }
}
