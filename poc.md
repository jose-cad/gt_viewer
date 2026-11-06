# Prova de Conceito (PoC) — MeshCentral: Inventário e Comandos

Documento de validação da **Tarefa 2** da especificação (`spec.md:233`).

---

## 1. Configurações Confirmadas

### 1.1 Proxy Reverso Caddy e TLS
- **Porta:** 443 exposta para agentes e tráfego HTTPS; porta 80 para redirecionamentos.
- **Cabeçalhos:** `Referrer-Policy: no-referrer` injetado em todas as respostas.
- **Roteamento:**
  - `gtviewer.<dominio>` / `localhost` → Serviço web ASP.NET Core (`web:8080`)
  - `mesh.<dominio>` / `mesh.localhost` → Serviço MeshCentral (`meshcentral:4430`)

### 1.2 Configuração do MeshCentral (`config.json`)
- `tlsOffload: true`: Terminação TLS feita no Caddy; o MeshCentral escuta em HTTP interno (`port: 4430`).
- `trustedProxy: "caddy"`: Confia no proxy reverso para resolução correta de IP real dos agentes e técnicos.
- `aliasPort: 443`: O MeshCentral instrui os agentes a conectar na porta HTTPS padrão do Caddy.
- `certUrl`: Aponta para o Caddy para que o agente obtenha o hash do certificado do proxy sem erro `"Bad web cert hash"`.
- `WebRTC: false`: Desativado para evitar qualquer tentativa de conexão STUN externa na WAN.
- `userConsentFlags: 120`: (8 [prompt desktop] + 16 [prompt terminal] + 32 [prompt files] + 64 [barra de sessão ativa]).
- `consentTimeout: 60`: 60 segundos para resposta do usuário.
- `autoAcceptOnTimeout: false`: Recusa automática se não houver resposta (estrito LGPD).
- `consentMessages`: Mensagens personalizadas em português.

---

## 2. Mapeamento de Campos de Inventário (Confirmado)

Com base na inspeção do código-fonte do MeshCentral v1.3.1 (`meshctrl.js` e `meshuser.js`):

| Campo no `gt_viewer` | Origem no payload MeshCentral | Observações |
|---|---|---|
| **Número de Série** | `hardware.identifiers.bios_serial` | Extraído da BIOS via WMI/CIM do agente Windows. |
| **Modelo** | `hardware.identifiers.product_name` | Fallback para `node.name` se ausente. |
| **Fabricante** | `hardware.identifiers.chassis_manufacturer` | Fallback para `hardware.identifiers.board_vendor`. |
| **Sistema Operacional** | `node.osdesc` | Descrição completa (ex.: *Microsoft Windows 11 Pro 64-bit*). |
| **Arquitetura** | `hardware.windows.osinfo.OSArchitecture` | Ex.: `64-bit`. |
| **Usuários Logados** | `node.users` | Array de strings com os usuários atualmente na sessão interativa. |
| **Antivírus** | `node.av` | Array de objetos contendo `product`, `updated` (bool) e `enabled` (bool). |
| **Conexão / Online** | `(node.conn & 1) != 0` | Flag bitwise 1 indica agente conectado ativamente no momento. |

---

## 3. Protocolo de Comandos e Scripts

### 3.1 Envio de Comandos
Comandos são despachados via WebSocket no endpoint `/control.ashx`:
```json
{
  "action": "runcommands",
  "nodeids": [ "node//..." ],
  "type": 2,
  "cmds": "<script-powershell-fixo>",
  "runAsUser": 0,
  "reply": true,
  "responseid": "gtv_cmd_xxxx"
}
```
- `type: 2`: Execução em PowerShell.
- `runAsUser: 0`: Execução no contexto `NT AUTHORITY\SYSTEM` (privilégio total para gerenciamento).
- `reply: true`: Força o agente a devolver a saída completa do comando.

### 3.2 Resposta e Marcadores da Spec
O MeshCentral devolve a mensagem:
```json
{
  "action": "msg",
  "type": "runcommands",
  "nodeid": "node//...",
  "result": "<saída do console>",
  "responseid": "gtv_cmd_xxxx"
}
```
Todo script executado imprime obrigatoriamente no final:
```powershell
Write-Output "GTV-RESULT=<VALOR>"
Write-Output "GTV-EXIT=<CODIGO>"
```
O cliente C# (`MeshCentralPocClient.ParseRunCommandResult`) extrai:
- `GtvResult`: Valor retornado (ex.: `OK`, `JA_INSTALADO`, `BLOQUEADA`, `OCUPADO`).
- `ExitCode`: Código numérico (onde `0` representa sucesso estrito).

---

## 4. Instruções para Validação nas VMs Windows de Teste

Para executar a validação final da Tarefa 2 quando as VMs de teste (uma em grupo de trabalho e outra no domínio) estiverem disponíveis:

1. **Obter o instalador do agente:**
   Execute no host ou via C#:
   ```bash
   docker exec gtv_meshcentral node /opt/meshcentral/meshcentral/meshctrl.js GenerateInviteLink --group "Notebooks" --hours 24 --url ws://127.0.0.1:4430 --loginuser admin --loginpass admin_dev_password_123
   ```
2. **Instalar nas duas VMs Windows:**
   - Acesse o link ou instale o serviço `MeshAgent.exe` em modo de serviço nas duas VMs.
   - O agente conectará via HTTPS através do Caddy (porta 443).
3. **Executar a ferramenta de teste PoC:**
   ```bash
   dotnet run --project tools/GtViewer.Poc
   ```
   A ferramenta listará as VMs conectadas, exibirá os campos de inventário e executará o script de teste validando `GTV-RESULT=OK` e `GTV-EXIT=0`.
4. **Validar evento de reconexão (`nodeconnect`):**
   ```bash
   dotnet run --project tools/GtViewer.Poc listen
   ```
   Reinicie uma das VMs e confirme o recebimento do evento em tempo real no console.
5. **Verificação de isolamento de tráfego:**
   Capture o tráfego com `tcpdump` no host ou Wireshark na VM para confirmar ausência de qualquer destino externo à WAN da empresa.
