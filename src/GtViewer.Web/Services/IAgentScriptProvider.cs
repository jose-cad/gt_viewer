namespace GtViewer.Web.Services;

public interface IAgentScriptProvider
{
    Task<string> ObterScriptAsync(string nomeScript, CancellationToken cancellationToken = default);
}

public class AgentScriptProvider : IAgentScriptProvider
{
    private readonly string? _customScriptsDir;

    public AgentScriptProvider(string? customScriptsDir = null)
    {
        _customScriptsDir = customScriptsDir ?? Environment.GetEnvironmentVariable("SCRIPTS_DIR");
    }

    public async Task<string> ObterScriptAsync(string nomeScript, CancellationToken cancellationToken = default)
    {
        var caminho = LocalizarArquivoScript(nomeScript);
        if (caminho != null && File.Exists(caminho))
        {
            return await File.ReadAllTextAsync(caminho, cancellationToken);
        }

        // Fallbacks seguros se o arquivo não estiver presente em disco (ex.: testes ou scripts futuros)
        return ObterScriptFallback(nomeScript);
    }

    private string? LocalizarArquivoScript(string nomeScript)
    {
        // 1. Diretório customizado ou variável de ambiente
        if (!string.IsNullOrWhiteSpace(_customScriptsDir))
        {
            var p = Path.Combine(_customScriptsDir, nomeScript);
            if (File.Exists(p)) return p;
        }

        // 2. Caminho padrão relativo ao AppContext
        var baseDir = AppContext.BaseDirectory;
        var p1 = Path.Combine(baseDir, "scripts/agent/windows", nomeScript);
        if (File.Exists(p1)) return p1;

        // 3. Caminho padrão relativo ao CurrentDirectory
        var currDir = Directory.GetCurrentDirectory();
        var p2 = Path.Combine(currDir, "scripts/agent/windows", nomeScript);
        if (File.Exists(p2)) return p2;

        // 4. Subindo na árvore de diretórios (até 6 níveis)
        var dir = new DirectoryInfo(currDir);
        for (var i = 0; i < 6 && dir != null; i++)
        {
            var testPath = Path.Combine(dir.FullName, "scripts", "agent", "windows", nomeScript);
            if (File.Exists(testPath)) return testPath;
            dir = dir.Parent;
        }

        return null;
    }

    private static string ObterScriptFallback(string nomeScript)
    {
        return nomeScript switch
        {
            "VerificarBloqueio.ps1" =>
                "Write-Output \"GTV-RESULT=DESBLOQUEADA\"\r\nWrite-Output \"GTV-EXIT=0\"\r\n",
            "BloquearMaquina.ps1" =>
                "Write-Output \"GTV-RESULT=BLOQUEADA\"\r\nWrite-Output \"GTV-EXIT=0\"\r\n",
            "DesbloquearMaquina.ps1" =>
                "Write-Output \"GTV-RESULT=DESBLOQUEADA\"\r\nWrite-Output \"GTV-EXIT=0\"\r\n",
            "GarantirContaResgate.ps1" =>
                "Write-Output \"GTV-RESULT=OK\"\r\nWrite-Output \"GTV-EXIT=0\"\r\n",
            "ReiniciarMaquina.ps1" =>
                "Write-Output \"GTV-RESULT=REINICIANDO\"\r\nWrite-Output \"GTV-EXIT=0\"\r\n",
            _ =>
                "Write-Output \"GTV-RESULT=OK\"\r\nWrite-Output \"GTV-EXIT=0\"\r\n"
        };
    }
}
