using System.Text.Json;
using System.Text.Json.Nodes;
using GtViewer.Poc;
using GtViewer.Web.Models;
using GtViewer.Web.Services;

Console.WriteLine("=================================================");
Console.WriteLine(" gt_viewer - Teste de Conceito: MeshCentral (PoC)");
Console.WriteLine("=================================================");

var url = Environment.GetEnvironmentVariable("MESHCENTRAL_URL") ?? "wss://mesh.localhost/control.ashx";
var user = Environment.GetEnvironmentVariable("MESHCENTRAL_USER") ?? "admin";
var pass = Environment.GetEnvironmentVariable("MESHCENTRAL_PASS") ?? "admin_dev_password_123";

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
var targetNodeId = args.Length > 1 ? args[1] : null;
var extraArg = args.Length > 2 ? args[2] : null;

var hexKey = Environment.GetEnvironmentVariable("MESHCENTRAL_COOKIE_KEY") ??
             "9ae4fb0b32a70e6fecb808f07ed6259ecb5336327b9aef2ac2c46b9d0b0f10a3ddfa47147b4c6dfe484a2042e255b07def08ca68d26c4ad0bc782ead178700525f3b707ab9493c940ff40b9cddfdc3c0";
var tokenService = new MeshLoginTokenService(hexKey, "https://mesh.localhost");

if (command == "token")
{
    var targetUser = targetNodeId ?? "joao";
    var mirrorUser = targetUser.StartsWith("gtv_mirror_") ? targetUser : $"gtv_mirror_{targetUser}";
    var token = tokenService.GenerateLoginToken(mirrorUser, expireMinutes: 2);
    var sampleNode = extraArg ?? "node//sample_node_123";
    var remoteUrl = tokenService.BuildRemoteDesktopUrl(token, sampleNode, viewmode: 11, hide: 31);

    Console.WriteLine($"[PoC] Token gerado para usuário espelho '{mirrorUser}':");
    Console.WriteLine($"Token: {token}");
    Console.WriteLine($"URL Iframe (viewmode=11, hide=31):");
    Console.WriteLine(remoteUrl);

    var decrypted = tokenService.DecryptLoginToken(token);
    Console.WriteLine($"Decifrado com sucesso: User={decrypted?.User}, Expire={decrypted?.ExpireMinutes}min, Time={decrypted?.Time}");
    Console.WriteLine($"Válido agora: {tokenService.IsTokenValid(decrypted)}");
    return;
}

await using var client = new MeshCentralPocClient(new Uri(url), user, pass);

try
{
    await client.ConnectAsync();

    // 1. Server info check
    var serverInfo = await client.GetServerInfoAsync(TimeSpan.FromSeconds(5));
    if (serverInfo != null)
    {
        Console.WriteLine($"[PoC] Servidor: {serverInfo["name"]} (Versão/Features: {serverInfo["features"]})");
        Console.WriteLine($"[PoC] Consentimento configurado: {serverInfo["consent"]}");
    }

    if (command == "listen")
    {
        Console.WriteLine("[PoC] Modo de escuta de eventos ativado. Aguardando 'nodeconnect'... Pressione Ctrl+C para sair.");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        await tcs.Task;
        return;
    }

    // Comandos de Suporte Remoto da Tarefa 4
    if (command == "mirror")
    {
        var targetUser = targetNodeId ?? "joao";
        var mirrorUser = await client.EnsureMirrorUserAsync(targetUser, TimeSpan.FromSeconds(5));
        Console.WriteLine($"[PoC] Usuário espelho garantido no MeshCentral: '{mirrorUser}'");
        return;
    }

    if (command == "grant-remote")
    {
        if (string.IsNullOrEmpty(targetNodeId))
        {
            Console.WriteLine("[PoC] Erro: NodeId obrigatorio.");
            Console.WriteLine("Uso: dotnet run --project tools/GtViewer.Poc grant-remote <nodeId> [usuario_tecnico]");
            return;
        }

        var targetUser = extraArg ?? "joao";
        var mirrorUser = await client.EnsureMirrorUserAsync(targetUser, TimeSpan.FromSeconds(5));
        var res = await client.GrantRemoteDesktopRightsAsync(targetNodeId, mirrorUser, TimeSpan.FromSeconds(5));
        Console.WriteLine($"[PoC] Direitos de tela concedidos: NodeId='{targetNodeId}', Usuário='{mirrorUser}', Rights=8 (0x08). Resultado: {res}");
        return;
    }

    if (command == "revoke-remote")
    {
        if (string.IsNullOrEmpty(targetNodeId))
        {
            Console.WriteLine("[PoC] Erro: NodeId obrigatorio.");
            Console.WriteLine("Uso: dotnet run --project tools/GtViewer.Poc revoke-remote <nodeId> [usuario_tecnico]");
            return;
        }

        var targetUser = extraArg ?? "joao";
        var res = await client.RevokeRemoteDesktopRightsAsync(targetNodeId, targetUser, TimeSpan.FromSeconds(5));
        Console.WriteLine($"[PoC] Direitos revogados no nó: NodeId='{targetNodeId}', Usuário='{targetUser}'. Resultado: {res}");
        return;
    }

    if (command == "drop-session")
    {
        var targetUser = targetNodeId ?? "joao";
        var res = await client.DropUserSessionsAsync(targetUser, TimeSpan.FromSeconds(5));
        Console.WriteLine($"[PoC] Sessões encerradas: Usuário='{targetUser}'. Resposta: {res}");
        return;
    }

    // Scripts de Tarefa 3 (Bloqueio / Resgate)
    if (command is "rescue" or "lock" or "unlock" or "checklock")
    {
        if (string.IsNullOrEmpty(targetNodeId))
        {
            Console.WriteLine($"[PoC] Erro: NodeId obrigatorio para o comando '{command}'.");
            Console.WriteLine("Uso: dotnet run --project tools/GtViewer.Poc <rescue|lock|unlock|checklock> <nodeId> [argumento_extra]");
            return;
        }

        var scriptsBaseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts/agent/windows"));
        if (!Directory.Exists(scriptsBaseDir))
        {
            scriptsBaseDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "scripts/agent/windows"));
        }

        string scriptName = command switch
        {
            "rescue" => "GarantirContaResgate.ps1",
            "lock" => "BloquearMaquina.ps1",
            "unlock" => "DesbloquearMaquina.ps1",
            "checklock" => "VerificarBloqueio.ps1",
            _ => throw new InvalidOperationException()
        };

        var scriptPath = Path.Combine(scriptsBaseDir, scriptName);
        if (!File.Exists(scriptPath))
        {
            Console.WriteLine($"[PoC] Erro: Script nao encontrado em {scriptPath}");
            return;
        }

        var scriptContent = await File.ReadAllTextAsync(scriptPath);

        // Parametrização se necessária
        if (command == "rescue" && !string.IsNullOrEmpty(extraArg))
        {
            scriptContent = $"$Password = '{extraArg.Replace("'", "''")}';\n" + scriptContent;
        }
        else if (command == "lock" && !string.IsNullOrEmpty(extraArg))
        {
            scriptContent = $"$ContactMessage = '{extraArg.Replace("'", "''")}';\n" + scriptContent;
        }

        Console.WriteLine($"[PoC] Enviando script '{scriptName}' para node {targetNodeId} via SYSTEM...");
        var cmdResult = await client.RunFixedScriptAsync(targetNodeId, scriptContent, TimeSpan.FromSeconds(20));

        Console.WriteLine($"\n[PoC] Resultado da Execução:");
        Console.WriteLine($"  -> GTV-RESULT: {cmdResult.GtvResult}");
        Console.WriteLine($"  -> GTV-EXIT:   {cmdResult.ExitCode}");
        Console.WriteLine($"  -> Saída Bruta:\n{cmdResult.RawOutput}");
        return;
    }

    // 2. Nodes / Inventory
    Console.WriteLine("\n[PoC] Consultando nós / computadores...");
    var nodesResponse = await client.GetNodesAsync(TimeSpan.FromSeconds(5));
    var nodesDict = nodesResponse?["nodes"]?.AsObject();

    var nodeList = new List<JsonNode>();
    if (nodesDict != null)
    {
        foreach (var meshProp in nodesDict)
        {
            var meshNodes = meshProp.Value?.AsObject();
            if (meshNodes != null)
            {
                foreach (var nodeProp in meshNodes)
                {
                    if (nodeProp.Value != null) nodeList.Add(nodeProp.Value);
                }
            }
        }
    }

    Console.WriteLine($"[PoC] Total de dispositivos encontrados: {nodeList.Count}");

    foreach (var node in nodeList)
    {
        var id = node["_id"]?.ToString() ?? "";
        var name = node["name"]?.ToString() ?? "(sem nome)";
        var osdesc = node["osdesc"]?.ToString() ?? "(desconhecido)";
        var conn = (node["conn"]?.GetValue<int>() ?? 0) & 1;
        var isOnline = conn != 0;

        Console.WriteLine($"\n-------------------------------------------------");
        Console.WriteLine($"Dispositivo: {name} (ID: {id})");
        Console.WriteLine($"Status: {(isOnline ? "ONLINE" : "OFFLINE")}");
        Console.WriteLine($"SO: {osdesc}");

        if (command == "nodes") continue;

        // If target specified or running all
        if (targetNodeId == null || targetNodeId == id)
        {
            // 3. Sysinfo
            Console.WriteLine($"[PoC] Consultando sysinfo para {id}...");
            try
            {
                var sysinfo = await client.GetSysInfoAsync(id, TimeSpan.FromSeconds(5));
                DisplayExtractedSysInfo(node, sysinfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PoC] Erro ao obter sysinfo: {ex.Message}");
            }

            // 4. Executar script fixo (se online)
            if (isOnline)
            {
                Console.WriteLine($"\n[PoC] Executando script PowerShell fixo com SYSTEM...");
                var fixedScript = "$serial = (Get-CimInstance Win32_Bios).SerialNumber; " +
                                  "Write-Output \"GTV-RESULT=OK SERIAL=$serial\"; " +
                                  "Write-Output \"GTV-EXIT=0\"";

                try
                {
                    var cmdResult = await client.RunFixedScriptAsync(id, fixedScript, TimeSpan.FromSeconds(15));
                    Console.WriteLine($"[PoC] GTV-RESULT: {cmdResult.GtvResult}");
                    Console.WriteLine($"[PoC] GTV-EXIT:   {cmdResult.ExitCode}");
                    Console.WriteLine($"[PoC] Saída bruta:\n{cmdResult.RawOutput}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PoC] Erro na execução de script: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("[PoC] Máquina offline; envio de comandos em tempo real requer agente conectado.");
            }
        }
    }

    Console.WriteLine("\n=================================================");
    Console.WriteLine(" PoC finalizada com sucesso!");
    Console.WriteLine("=================================================");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[ERRO FATAL] {ex}");
}

static void DisplayExtractedSysInfo(JsonNode node, JsonNode? sysinfo)
{
    var ident = sysinfo?["hardware"]?["identifiers"];
    var biosSerial = ident?["bios_serial"]?.ToString() ?? "(não detectado)";
    var productName = ident?["product_name"]?.ToString() ?? "(não detectado)";
    var manufacturer = ident?["chassis_manufacturer"]?.ToString() ??
                       ident?["board_vendor"]?.ToString() ?? "(não detectado)";
    var osdesc = node["osdesc"]?.ToString() ?? "(não detectado)";

    Console.WriteLine($"  -> Número de Série: {biosSerial}");
    Console.WriteLine($"  -> Modelo:           {productName}");
    Console.WriteLine($"  -> Fabricante:       {manufacturer}");
    Console.WriteLine($"  -> Sistema Operacional: {osdesc}");

    // Antivírus
    var avArray = node["av"]?.AsArray();
    if (avArray != null && avArray.Count > 0)
    {
        Console.WriteLine("  -> Antivírus detectado(s):");
        foreach (var avItem in avArray)
        {
            var prod = avItem?["product"]?.ToString();
            var updated = avItem?["updated"]?.ToString();
            var enabled = avItem?["enabled"]?.ToString();
            Console.WriteLine($"     * {prod} (Atualizado: {updated}, Ativo: {enabled})");
        }
    }
    else
    {
        Console.WriteLine("  -> Antivírus: Nenhum produto reportado");
    }

    // Usuários logados
    var usersArray = node["users"]?.AsArray();
    if (usersArray != null && usersArray.Count > 0)
    {
        Console.WriteLine($"  -> Usuários logados: {string.Join(", ", usersArray.Select(u => u?.ToString()))}");
    }
    else
    {
        Console.WriteLine("  -> Usuários logados: (nenhum)");
    }
}
