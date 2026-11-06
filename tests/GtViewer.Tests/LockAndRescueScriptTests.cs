using GtViewer.Poc;
using Xunit;

namespace GtViewer.Tests;

public class LockAndRescueScriptTests
{
    private readonly string _scriptsDir;

    public LockAndRescueScriptTests()
    {
        _scriptsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts/agent/windows"));
        if (!Directory.Exists(_scriptsDir))
        {
            _scriptsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "scripts/agent/windows"));
        }
    }

    [Fact]
    public void AllRequiredLockAndRescueScripts_ExistInRepository()
    {
        Assert.True(File.Exists(Path.Combine(_scriptsDir, "GarantirContaResgate.ps1")), "GarantirContaResgate.ps1 deve existir");
        Assert.True(File.Exists(Path.Combine(_scriptsDir, "BloquearMaquina.ps1")), "BloquearMaquina.ps1 deve existir");
        Assert.True(File.Exists(Path.Combine(_scriptsDir, "DesbloquearMaquina.ps1")), "DesbloquearMaquina.ps1 deve existir");
        Assert.True(File.Exists(Path.Combine(_scriptsDir, "VerificarBloqueio.ps1")), "VerificarBloqueio.ps1 deve existir");
    }

    [Fact]
    public void GarantirContaResgateScript_MeetsSpecInvariants()
    {
        var content = File.ReadAllText(Path.Combine(_scriptsDir, "GarantirContaResgate.ps1"));

        Assert.Contains("gtv-resgate", content);
        Assert.Contains("S-1-5-32-544", content); // Resolução agnóstica de idioma para Administradores
        Assert.Contains("PasswordNeverExpires", content);
        Assert.Contains("Unlock-LocalUser", content);
        Assert.Contains("GTV-RESULT=OK", content);
        Assert.Contains("GTV-EXIT=0", content);
        Assert.Contains("Revealed", content);
    }

    [Fact]
    public void BloquearMaquinaScript_MeetsSpecInvariants()
    {
        var content = File.ReadAllText(Path.Combine(_scriptsDir, "BloquearMaquina.ps1"));

        Assert.Contains("gtv-resgate", content);
        Assert.Contains("FALHA_SEM_CONTA_RESGATE", content);
        Assert.Contains("lock.marker", content);
        Assert.Contains("original_sec.cfg", content);
        Assert.Contains("secedit", content);
        Assert.Contains("SeInteractiveLogonRight", content);
        Assert.Contains("SeDenyInteractiveLogonRight", content);
        Assert.Contains("LegalNoticeCaption", content);
        Assert.Contains("LegalNoticeText", content);
        Assert.Contains("schtasks", content);
        Assert.Contains("GtvReapplyLock", content);
        Assert.Contains("logoff", content);
        Assert.Contains("GTV-RESULT=BLOQUEADA", content);
        Assert.Contains("GTV-EXIT=0", content);
    }

    [Fact]
    public void DesbloquearMaquinaScript_MeetsSpecInvariants()
    {
        var content = File.ReadAllText(Path.Combine(_scriptsDir, "DesbloquearMaquina.ps1"));

        Assert.Contains("original_sec.cfg", content);
        Assert.Contains("secedit", content);
        Assert.Contains("LegalNoticeCaption", content);
        Assert.Contains("schtasks /delete", content);
        Assert.Contains("GtvReapplyLock", content);
        Assert.Contains("lock.marker", content);
        Assert.Contains("GTV-RESULT=DESBLOQUEADA", content);
        Assert.Contains("GTV-EXIT=0", content);
    }

    [Fact]
    public void VerificarBloqueioScript_MeetsSpecInvariants()
    {
        var content = File.ReadAllText(Path.Combine(_scriptsDir, "VerificarBloqueio.ps1"));

        Assert.Contains("lock.marker", content);
        Assert.Contains("lock.inf", content);
        Assert.Contains("secedit", content);
        Assert.Contains("GTV-RESULT=BLOQUEADA", content);
        Assert.Contains("GTV-RESULT=DESBLOQUEADA", content);
        Assert.Contains("GTV-EXIT=0", content);
    }

    [Theory]
    [InlineData("GTV-RESULT=BLOQUEADA\r\nGTV-EXIT=0", "BLOQUEADA", 0)]
    [InlineData("GTV-RESULT=DESBLOQUEADA\r\nGTV-EXIT=0", "DESBLOQUEADA", 0)]
    [InlineData("GTV-RESULT=FALHA_SEM_CONTA_RESGATE\r\nGTV-EXIT=1", "FALHA_SEM_CONTA_RESGATE", 1)]
    [InlineData("GTV-RESULT=FALHA_SENHA_NAO_INFORMADA\r\nGTV-EXIT=1", "FALHA_SENHA_NAO_INFORMADA", 1)]
    public void ParseRunCommandResult_LockAndRescueMarkers_ExtractedAccurately(string output, string expectedResult, int expectedExit)
    {
        var parsed = MeshCentralPocClient.ParseRunCommandResult(output);
        Assert.Equal(expectedResult, parsed.GtvResult);
        Assert.Equal(expectedExit, parsed.ExitCode);
    }
}
