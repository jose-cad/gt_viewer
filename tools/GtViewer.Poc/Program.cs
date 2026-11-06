using System.Text.Json;
using System.Text.Json.Nodes;
using GtViewer.Poc;

Console.WriteLine("=================================================");
Console.WriteLine(" gt_viewer - Teste de Conceito: MeshCentral (PoC)");
Console.WriteLine("=================================================");

var url = Environment.GetEnvironmentVariable("MESHCENTRAL_URL") ?? "wss://localhost/control.ashx";
var user = Environment.GetEnvironmentVariable("MESHCENTRAL_USER") ?? "admin";
var pass = Environment.GetEnvironmentVariable("MESHCENTRAL_PASS") ?? "admin_dev_password_123";

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
var targetNodeId = args.Length > 1 ? args[1] : null;

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
