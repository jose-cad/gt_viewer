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

---

## 5. Teste de Conceito — Bloqueio e Conta de Resgate (Tarefa 3)

Implementação dos scripts PowerShell de gerenciamento local e blindagem de segurança (`spec.md:234`, seção 2.5).

### 5.1 Scripts Criados (`scripts/agent/windows/`)

1. **`GarantirContaResgate.ps1`:**
   - Cria/atualiza o usuário local `gtv-resgate`.
   - Adiciona ao grupo Administradores por SID `S-1-5-32-544` (agnóstico ao idioma do SO).
   - Configura `PasswordNeverExpires = $true`, `UserMayNotChangePassword = $true`, `Enabled = $true` e `Unlock-LocalUser`.
   - Se invocado com `-Revealed`, encerra sessões ativas da conta `gtv-resgate`.
   - Retorna: `GTV-RESULT=OK` e `GTV-EXIT=0`.

2. **`BloquearMaquina.ps1`:**
   - Pré-condição: aborta com `GTV-RESULT=FALHA_SEM_CONTA_RESGATE` se a conta `gtv-resgate` não existir ou estiver inválida.
   - Backup idempotente: salva `original_sec.cfg` e chaves `LegalNotice` originais em `C:\ProgramData\gt_viewer\`; se o arquivo `lock.marker` já existir, **não sobrescreve o backup original**.
   - Restrição local: aplica template `secedit` concedendo `SeInteractiveLogonRight` exclusivamente ao SID de `gtv-resgate` e negando a `Authenticated Users` (`S-1-5-11`) e `Users` (`S-1-5-32-545`).
   - Mensagem de bloqueio: atualiza `LegalNoticeCaption` e `LegalNoticeText`.
   - Persistência contra GPO / reinicialização: cria tarefas agendadas locais (`GtvReapplyLock` e `GtvReapplyLockStartup`) que executam a cada 5 minutos como `SYSTEM` reaplicando o bloqueio caso um `gpupdate /force` do domínio rode com a máquina ligada.
   - Encerra todas as sessões interativas abertas (`logoff`).
   - Retorna: `GTV-RESULT=BLOQUEADA` e `GTV-EXIT=0`.

3. **`DesbloquearMaquina.ps1`:**
   - Restaura as diretivas originais de `original_sec.cfg` via `secedit`.
   - Restaura ou limpa as mensagens de login `LegalNotice`.
   - Remove as tarefas agendadas locais de persistência.
   - Remove o marcador `lock.marker` e arquivos temporários de bloqueio.
   - Encerra qualquer sessão ativa da conta `gtv-resgate`.
   - Retorna: `GTV-RESULT=DESBLOQUEADA` e `GTV-EXIT=0`.

4. **`VerificarBloqueio.ps1`:**
   - Verifica a existência de `lock.marker`. Se presente, reaplica a política de segurança, encerra sessões de `gtv-resgate` e retorna `GTV-RESULT=BLOQUEADA`.
   - Se ausente, retorna `GTV-RESULT=DESBLOQUEADA`.

### 5.2 Validação nas VMs Windows via Ferramenta PoC

Com a VM online no MeshCentral:

```bash
# 1. Garantir conta de resgate com senha aleatória
dotnet run --project tools/GtViewer.Poc rescue <nodeId> "SenhaForteResgate!2026"

# 2. Bloquear a máquina
dotnet run --project tools/GtViewer.Poc lock <nodeId> "Equipamento bloqueado pela TI. Contato: ti@empresa.com.br"

# 3. Conferir na VM bloqueada:
#    - Tentativa de login com usuário comum/domínio é recusada ("O tipo de logon não é permitido").
#    - Login com gtv-resgate entra normalmente.
#    - Executar `gpupdate /force` na VM e aguardar 5 min: a tarefa periódica mantém o bloqueio ativo.
#    - Reiniciar a VM: continua bloqueada na tela de login com a mensagem visível e Wi-Fi/cabo funcionando.

# 4. Desbloquear a máquina remotamente
dotnet run --project tools/GtViewer.Poc unlock <nodeId>

# 5. Conferir na VM desbloqueada:
#    - Usuários do domínio/locais entram normalmente.
#    - Mensagem de aviso desapareceu.
```

---

## 6. Teste de Conceito — Tela Remota (Tarefa 4)

Validação de arquitetura e implementação dos requisitos de suporte remoto (`spec.md:235`, seções 2.1 e 2.6).

### 6.1 Código Exato de `viewmode` e Parâmetro `hide`

Com base na inspeção de `default.handlebars` (linhas 20520-20560 e 2095-2140) no MeshCentral v1.3.1:

| Parâmetro | Valor | Descrição e Comportamento |
|---|---|---|
| **`viewmode=11`** | `MainDevDesktop` | **Selecionado para o gt_viewer**. Abre diretamente o canvas interativo de controle remoto e aciona imediatamente `deskAdjust()` para dimensionamento responsivo da tela. |
| **`viewmode=10`** | `MainDev` | Abre a aba geral do dispositivo (visão de abas: Geral, Console, Terminal, Arquivos), exigindo navegação manual até o desktop. |
| **`hide=31`** | Soma dos bits 1 + 2 + 4 + 8 + 16 | Oculta toda a interface externa do MeshCentral dentro do iframe:<br>• Bit 1 (`1`): Barra superior (Top bar)<br>• Bit 2 (`2`): Barra de ferramentas (Tool bar)<br>• Bit 4 (`4`): Rodapé (Bottom bar)<br>• Bit 8 (`8`): Título da aba (Tab title)<br>• Bit 16 (`16`): Menu lateral de navegação (Left bar / MainMenu)<br>**Resultado:** Apenas o canvas da tela remota é renderizado no iframe. |

### 6.2 Direitos Mínimos da Conta e do Usuário Espelho

Com base na definição do MeshCentral v1.3.1 (`apprelays.js:38-61` e `meshuser.js:6070-6085`):

```csharp
public const long MESHRIGHT_REMOTECONTROL = 0x00000008; // 8 - Ver e controlar tela
```

- **Usuário Espelho do Técnico:**
  - Identificador: `user//gtv_mirror_<username>` (sem senha, criado via `adduser` com `siteadmin: 0`).
  - Bitmask concedido: **estritamente `8` (`MESHRIGHT_REMOTECONTROL`)**.
  - Bits sensíveis zerados (`0`):
    - `MESHRIGHT_AGENTCONSOLE = 16` (Terminal / Console): Bloqueado (`rights & 16 == 0`).
    - `MESHRIGHT_SERVERFILES = 32` (Arquivos): Bloqueado (`rights & 32 == 0`).
    - `MESHRIGHT_REMOTECOMMAND = 131072` (Comandos arbitrários): Bloqueado (`rights & 131072 == 0`).
    - `MESHRIGHT_MANAGEUSERS = 2` e `MESHRIGHT_MANAGECOMPUTERS = 4`: Bloqueados.
  - Regra nativa do MeshCentral: O método `adddeviceuser` (`meshuser.js:6075`) recusa qualquer atribuição com `rights & 7 != 0` em nível de dispositivo, validando que permissões estruturais pertencem apenas a grupos, e links de dispositivo só podem receber `8` (controle remoto), `16`, `32`, etc.
- **Conta de Serviço (`gtv_service` / admin):**
  - Conta interna que o backend do gt_viewer utiliza via WebSocket (`/control.ashx`).
  - Possui `siteadmin: 0xFFFFFFFF` para poder gerenciar usuários espelho (`adduser`, `users`), atribuir direitos (`adddeviceuser`) e forçar encerramento de sessões (`closeusersessions`).

### 6.3 Criptografia do Token de Login (AES-256-GCM)

Compatibilidade bidirecional validada entre C# (.NET 10 `System.Security.Cryptography.AesGcm`) e Node.js (`decodeCookieAESGCM` em `meshcentral.js:4000`):

1. **Chave de Criptografia:**
   - 80 bytes (160 caracteres hexadecimais), fixada em `config/meshcentral/config.json` sob `settings.loginCookieEncryptionKey`.
   - O MeshCentral utiliza os primeiros 32 bytes (`key.slice(0, 32)`) para o algoritmo `aes-256-gcm`.
2. **Payload JSON:**
   ```json
   {
     "u": "user//gtv_mirror_<username>",
     "a": 3,
     "expire": 2,
     "time": 1793980926
   }
   ```
   - `u`: Usuário espelho no domínio padrão (`user//<nome>`).
   - `a: 3`: Identificador de token de login único.
   - `expire: 2`: Validade em minutos (2 minutos).
   - `time`: Unix timestamp em segundos no momento da geração.
3. **Estrutura Binária do Token:**
   - `[12 bytes IV]` + `[16 bytes AuthTag]` + `[Ciphertext]`.
   - Codificado em Base64 com substituições: `+` vira `@` e `/` vira `$`.
4. **URL Final no iframe:**
   ```
   https://mesh.<dominio>/?login=<token>&gotonode=<nodeId>&viewmode=11&hide=31
   ```

### 6.4 Derrubada Ativa de Sessão / Túnel e Detecção de Consentimento

1. **Encerramento Imediato da Sessão:**
   - Para encerrar ativamente a sessão e o túnel de tela (ao clicar "Encerrar", ao expirar 60 min, ou na detecção de saída do usuário):
     1. Disparo de comando de console via WebSocket:
        `{ "action": "serverconsole", "value": "closeusersessions gtv_mirror_<username>" }`
        O MeshCentral envia `stopped: "Administrator forced disconnection"` para o WebSocket do navegador no iframe e fecha os sockets de relay (`ws.close()`), derrubando imediatamente o túnel.
     2. Revogação atômica dos direitos no dispositivo:
        `{ "action": "adddeviceuser", "nodeid": "<nodeId>", "usernames": ["gtv_mirror_<username>"], "remove": true }`
        Garante que qualquer tentativa residual de reconexão seja rejeitada com `Access denied`.
2. **Detecção do Desfecho do Consentimento:**
   - O agente Windows (`meshcore.js`) emite eventos estruturados recebidos no `control.ashx`:
     - `msgid: 30`: *"Starting remote desktop after local user accepted"* → Consentimento concedido; o gt_viewer confirma o início da sessão.
     - `msgid: 34`: *"Failed to start remote desktop after local user rejected"* → Usuário clicou em recusar ou o timeout de 60 segundos expirou sem resposta. O gt_viewer detecta `msgid: 34`, libera a trava atômica no banco e revoga o direito no MeshCentral.
     - `msgid: 29`: *"Remote Desktop Connection forcefully closed by local user"* → Usuário encerrou o suporte pela barra de privacidade fixa.
     - `msgid: 53`: *"Locking remote user out of desktop"* → Bloqueio de tela (`Win+L`) ou encerramento de sessão do usuário no Windows. A sessão de tela é terminada na hora.

### 6.5 Matriz de Caminhos do Proxy Reverso (Caddy)

Lista fechada de caminhos permitidos na porta 443:

| Domínio | Caminho | Acesso Permitido | Finalidade |
|---|---|---|---|
| `mesh.<dominio>` | `/agent.ashx*` | **Público (WAN e Interno)** | Conexão principal do agente MeshCentral (WebSocket e HTTP). |
| `mesh.<dominio>` | `/meshrelay.ashx*` | **Público (WAN e Interno)** | Túnel de dados de relay entre o agente e o servidor. |
| `mesh.<dominio>` | `/` (com token) | **Rede Interna Apenas** | Renderização do iframe de tela remota (`viewmode=11&hide=31`). |
| `mesh.<dominio>` | `/styles/*`, `/scripts/*`, `/images/*`, `/fonts/*` | **Rede Interna Apenas** | Assets estáticos para execução do canvas no navegador do técnico. |
| `mesh.<dominio>` | `/control.ashx` | **Rede Interna Apenas** | Canal de controle do navegador no iframe e da conta de serviço. |
| `mesh.<dominio>` | Demais caminhos / Painel admin | **Rede Interna Apenas** | Restrito a administradores na rede corporativa. Bloqueado na WAN (403). |
| `gtviewer.<dominio>` | `/agente/*` | **Público (WAN e Interno)** | Endpoints de token temporário chamados pelos scripts das máquinas. |
| `gtviewer.<dominio>` | Todos os demais caminhos (`/`, `/RemoteSupport`, etc.) | **Rede Interna Apenas** | Interface web do gt_viewer. Bloqueado na WAN (403). |


