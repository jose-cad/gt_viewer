using GtViewer.Poc;
using Xunit;

namespace GtViewer.Tests;

public class PocProtocolTests
{
    [Theory]
    [InlineData("GTV-RESULT=OK\r\nGTV-EXIT=0", "OK", 0)]
    [InlineData("Some initial output...\nProcess completed.\nGTV-RESULT=JA_INSTALADO\nGTV-EXIT=0\nDone.", "JA_INSTALADO", 0)]
    [InlineData("GTV-RESULT=BLOQUEADA SERIAL=XYZ123\r\nGTV-EXIT=0", "BLOQUEADA SERIAL=XYZ123", 0)]
    [InlineData("Falha no PowerShell\nGTV-RESULT=FALHA_CONTA\nGTV-EXIT=5", "FALHA_CONTA", 5)]
    [InlineData("GTV-RESULT=OCUPADO\nGTV-EXIT=1618", "OCUPADO", 1618)]
    public void ParseRunCommandResult_ValidOutput_ExtractsResultAndExitCode(string output, string expectedResult, int expectedExit)
    {
        var result = MeshCentralPocClient.ParseRunCommandResult(output);

        Assert.Equal(expectedResult, result.GtvResult);
        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(output, result.RawOutput);
    }

    [Fact]
    public void ParseRunCommandResult_MissingMarkers_ReturnsDesconhecidoAndMinusOne()
    {
        var output = "Comando executado sem os marcadores padrão da spec";
        var result = MeshCentralPocClient.ParseRunCommandResult(output);

        Assert.Equal("DESCONHECIDO", result.GtvResult);
        Assert.Equal(-1, result.ExitCode);
    }
}
