STATUS: APPROVED (2026-11-06)

# Spec — gt_viewer v1: gerenciamento de computadores Windows

Base: [brief.md](brief.md) e [../invariants.md](../invariants.md). Revisão: [review.md](review.md).

## 1. Jornadas críticas

### Convenções de todas as jornadas
- **Permissões** (uma por ação; checadas no servidor em toda requisição; sem permissão, o botão não aparece e a requisição responde 403):
  `suporte_remoto`, `reiniciar`, `bloquear` (bloquear/desbloquear), `revelar_senha_resgate`, `instalar_apps`, `gerenciar_catalogo`, `gerenciar_grupos`, `importar_planilha`, `cadastrar_maquinas` (baixar instalador, corrigir série, resolver conflito), `ver_auditoria`, `administrar_tecnicos`. Ver o inventário é permitido a todos. **Administrador** = quem tem `administrar_tecnicos`.
- **Auditoria:** toda ação grava o registro em `Auditoria` na mesma transação do banco, **antes** de qualquer efeito; se a gravação falhar, nada acontece (invariante 5). Ações em uma máquina (fila: bloquear, desbloquear, garantir/trocar/redefinir conta de resgate, instalar, remover; imediatas: reiniciar, acessar tela) também criam/atualizam um registro na tabela `Acao` na mesma transação; ações administrativas e síncronas (login, planilha, técnicos, catálogo, grupos, revelar senha, aprovar/rejeitar/corrigir/unir máquina, baixar instalador, reconhecer alerta e comandos de CLI) gravam direto em `Auditoria`. Ações automáticas registram o ator "sistema". Tipos auditados (lista fechada, usada como filtro em J10 e checklist da tarefa 16): login ok/falho, criar primeiro administrador (CLI), habilitar/expirar conta de emergência, uso da conta de emergência, reconhecer alerta, encerrar sessão por desativação no AD, falha na reconferência do AD, acessar tela (início/fim/recusa), revogar direito órfão de tela, reiniciar, bloquear, desbloquear, verificar/reaplicar bloqueio na reconexão, cancelar pendência, revelar senha de resgate, trocar senha de resgate, garantir conta de resgate, redefinir conta de resgate, baixar instalador, aprovar/rejeitar máquina, corrigir série, unir registros, instalar, remover, alterar catálogo, alterar grupo, importar planilha (prévia e confirmação), criar/alterar/desativar técnico, redefinir 2FA, gerar código de ativação.
- **Estados** (três eixos independentes):
  - Conexão da máquina: `online` (agente conectado agora), `offline`, `sumida` (sem contato há mais de 30 dias).
  - Bloqueio da máquina: `desbloqueada`, `bloqueio pendente`, `bloqueada`, `desbloqueio pendente`, `falha`.
  - Ação (em máquina): `pendente`, `enviada`, `executada`, `falhou`, `resultado desconhecido`, `cancelada`.
- **Conexão com o MeshCentral caída:** todas as telas mostram "Sem conexão com o servidor de máquinas — dados de HH:MM"; reiniciar, acessar tela e revelar senha ficam indisponíveis; ações novas da fila ficam pendentes.

### J1. Entrar no sistema
1. Técnico informa usuário e senha do domínio.
2. O gt_viewer confere **primeiro** se o usuário está cadastrado e ativo no gt_viewer (J9) e aplica atraso progressivo por par usuário+IP após falhas (sem travar a conta inteira); só então consulta o AD via LDAPS (com validação do certificado do servidor). Isso impede que tentativas travem contas do domínio. Toda resposta de login leva o mesmo tempo mínimo, para não revelar quem é técnico.
3. Primeiro acesso (ou após redefinição da 2FA): além da senha, o técnico informa o **código de ativação** gerado pelo administrador (J9); só então cadastra o aplicativo autenticador (QR code) e confirma um código.
4. Acessos seguintes: pede o código de 6 dígitos do autenticador; um código já usado não é aceito de novo.
5. A sessão expira após 8 horas. A cada 15 minutos o gt_viewer reconfere a conta no AD; desativada → sessão encerrada. AD inacessível na reconferência → a sessão continua (até o limite de 8 h) e o fato é registrado.
- Erros: qualquer falha → mensagem genérica "credenciais inválidas". 5 códigos de 2FA errados → conta travada no gt_viewer por 15 minutos; mais de 10 tentativas falhas por minuto do mesmo IP → IP bloqueado por 15 minutos.
- Perdeu o celular do autenticador: um administrador redefine a 2FA e gera novo código de ativação.
- **Conta de emergência** (para AD fora do ar): conta local do gt_viewer, com senha longa e 2FA, credenciais guardadas pelo gestor de infra. Fica **desativada**; só o gestor a habilita, por comando no próprio servidor, e ela se desativa sozinha após 24 h, encerrando suas sessões abertas. Permissões **apenas**: ver inventário, `bloquear`, `reiniciar` e `suporte_remoto`; não conta como "último administrador" e não passa pela reconferência no AD. Cada login ou ação dela gera um alerta persistido em destaque para todos os administradores; qualquer administrador pode reconhecê-lo (encerrando o alerta), exceto a própria conta de emergência.
- **Primeiro administrador:** criado na implantação por comando no servidor, que gera seu código de ativação.

### J2. Cadastrar uma máquina
1. Na formatação, técnico com `cadastrar_maquinas` baixa o instalador do agente pelo gt_viewer (download auditado) e instala na máquina.
2. O agente conecta e a máquina aparece no inventário, identificada pelo número de série.
3. Série nova → o sistema agenda `garantir conta de resgate` para criar `gtv-resgate` (J6) com senha nova e única.
4. Série já conhecida (máquina reformatada ou outra máquina dizendo ter a mesma série) → **nunca substitui sozinho**. O novo agente fica em "Aguardando aprovação", sem nenhuma ação enviada a ele. A tela mostra, lado a lado para o agente antigo e o novo: hostname, IP, primeiro e último contato. Técnico com `cadastrar_maquinas` confere e:
   - **aprova a troca:** a máquina passa a apontar para o novo agente e o antigo é removido do MeshCentral; histórico é mantido; ações de bloqueio, desbloqueio e conta/senha de resgate pendentes ou em dúvida do agente antigo são canceladas e a candidata de senha antiga é descartada; instalações pendentes continuam (a detecção evita reinstalar). Uma nova ação de **garantir conta de resgate** (com senha nova) é agendada com prioridade máxima. Se o técnico também tiver a permissão `bloquear`, escolhe se o bloqueio continua (gera nova ação de bloqueio) ou é limpo (padrão: limpo; limpar o bloqueio de uma máquina `bloqueada` exige justificativa); se não tiver `bloquear`, o estado de bloqueio vigente é mantido obrigatoriamente (gerando nova ação de bloqueio caso a máquina estivesse `bloqueada` ou com `bloqueio pendente`);
   - **rejeita:** o novo agente é removido do MeshCentral.
   - Ambos online ao mesmo tempo → não é possível aprovar a troca; fica como conflito até um deles ser resolvido.
- Série ilegível ou genérica (lista: "To be filled by O.E.M.", "Default string", "System Serial Number", "0123456789", "None", vazio e similares) → máquina marcada "sem série", identificada provisoriamente pelo agente.
- **Correção manual** (permissão `cadastrar_maquinas`): na tela "Aguardando aprovação / Sem série", o técnico informa a série correta (marcada como definida manualmente para não ser sobrescrita pelo `bios_serial` nas sincronizações seguintes) ou une o registro a uma máquina existente, escolhendo qual registro prevalece (valem as mesmas regras de cancelamento e de permissão `bloquear` da aprovação); após unir, é agendado `garantir conta de resgate` com senha nova e o estado de bloqueio escolhido no banco é verificado/imposto na máquina. Tudo auditado.

### J3. Consultar inventário
1. Lista de máquinas com: série, modelo, fabricante, SO/versão, usuário logado, último contato, antivírus (ativo e atualizado / com problema / desconhecido), conexão, bloqueio, situação no contrato (no contrato, fora do contrato, sem planilha importada).
2. Filtros por conexão, bloqueio, situação no contrato e grupo; busca por série ou usuário.
3. Detalhe da máquina: dados acima, grupos, ações pendentes e últimas ações de auditoria dela.

### J4. Conferir o contrato da locadora
1. Técnico com `importar_planilha` envia a planilha Excel (.xlsx, até 5 MB) recebida por e-mail.
2. O sistema procura o cabeçalho da coluna de série nas 10 primeiras linhas, lê as séries **como texto** (preservando zeros à esquerda) e normaliza (maiúsculas, sem espaços).
3. Mostra a prévia, guardada com um identificador: linhas lidas, séries válidas, repetidas e linhas ignoradas.
4. Técnico confirma aquela prévia; ela passa a ser a lista vigente do contrato (a anterior fica no histórico).
5. Resultado em três listas: no contrato e no inventário; no contrato e nunca apareceu ou sumida; no inventário e fora do contrato.
- Erros: arquivo não é .xlsx, está corrompido ou passa de 5 MB → recusa com mensagem; coluna de série não encontrada → recusa indicando os nomes aceitos; 0 séries válidas → confirmação bloqueada; quantidade de séries cai mais de 20% em relação à vigente → pede confirmação reforçada. Valores da planilha são exibidos sempre como texto escapado.

### J5. Suporte remoto
1. No detalhe da máquina online (e com usuário logado em `Maquina.users`), técnico com `suporte_remoto` clica "Acessar tela".
2. O gt_viewer reserva a máquina para esse técnico (trava atômica no banco com identificador de sessão e o usuário logado no momento, renovada pela página a cada 30 s, expira sem renovação em 90 s). A trava só é liberada depois de confirmadas a derrubada da sessão e a revogação do direito. Outro técnico já em sessão → bloqueado com aviso de quem está conectado.
3. O iframe de tela remota é aberto no navegador do técnico e a conexão do túnel no MeshCentral dispara o pedido ao funcionário logado: "[Técnico] da TI quer acessar sua tela. Permitir?".
4. Permitiu → a tela é exibida no iframe do técnico, e o funcionário vê um aviso fixo de sessão ativa até o fim.
5. Recusou ou não respondeu em 60 segundos → técnico vê "acesso recusado" ou "sem resposta"; nada é transmitido; trava liberada e direito revogado na hora.
6. Fim: técnico encerra, funcionário encerra pelo aviso, funcionário faz logoff / bloqueia a tela (`Win+L`) / troca de usuário no Windows, queda de conexão, técnico fecha a aba (a página deixa de renovar a trava) ou limite de 60 minutos (para continuar, o técnico pede acesso de novo). Em todos os casos, a sessão de tela é derrubada ativamente, não só o direito revogado. Início, fim e recusa ficam registrados.
- **Sempre** é preciso a permissão de um usuário logado. Sem ninguém logado (tela de login ou máquina bloqueada) → não há acesso à tela; se o usuário que consentiu sair da sessão ativa durante o atendimento, o acesso é encerrado imediatamente (invariante 9).
- Máquina offline ou conexão com o MeshCentral caída → botão desabilitado.

### J6. Bloquear e desbloquear
1. Técnico com `bloquear` clica "Bloquear" em uma máquina, informa o motivo (obrigatório) e confirma.
2. Estado de bloqueio vira `bloqueio pendente`.
3. Quando a máquina estiver online, o bloqueio é aplicado: encerra as sessões abertas e impede login de qualquer conta, exceto a de resgate. Na tela de login aparece: "Equipamento bloqueado pela TI. Contato: [contato da TI]".
4. Estado vira `bloqueada`; se falhar, `falha` com o erro.
- O bloqueio **só é aplicado** se a conta de resgate (`gtv-resgate`) existir, estiver habilitada, desbloqueada, com senha que nunca expira (`PasswordNeverExpires`) e com senha atual confirmada; senão a ação falha sem restringir nada.
- Desbloqueio é sempre remoto, pelo gt_viewer, a qualquer momento: estado `desbloqueio pendente` até a máquina conectar; depois `desbloqueada`.
- A máquina bloqueada continua se conectando à internet na tela de login (Wi-Fi salvo ou cabo), então recebe o desbloqueio assim que conectar.
- Desbloquear com a ação de bloqueio ainda `pendente` → só cancela a pendência. Se ela já estiver `enviada` ou `resultado desconhecido`, o desbloqueio entra na fila logo depois e é aplicado em seguida.
- Estado `falha` permite "Desbloquear": a máquina verifica o estado real e desfaz o que tiver sido aplicado.
- Bloqueio é sempre uma máquina por vez (sem bloqueio em massa na v1).
- **Máquina bloqueada e sem nenhuma internet** (ex.: rede Wi-Fi nova): técnico com `revelar_senha_resgate` vê a senha de resgate — **disponível com a máquina em `bloqueada`, `desbloqueio pendente`, `falha` ou `bloqueio pendente` (quando a ação já foi `enviada` ou está em `resultado desconhecido`), offline há mais de 5 minutos segundo uma conexão ativa com o MeshCentral, e após informar de novo o código do autenticador** — e a passa por telefone ao funcionário, que entra com ela e conecta a máquina à internet. O técnico, ainda no telefone, clica "Desbloquear". A senha revelada fica marcada como "revelada": na primeira conexão seguinte, o sistema troca a senha de resgate antes de qualquer outra ação **e já encerra quaisquer sessões abertas da `gtv-resgate`** (além de encerrá-las também no desbloqueio). Revelar a senha é auditado.
- Limite conhecido: quem tem a senha de resgate é administrador local e poderia desfazer o bloqueio ou parar o agente; por isso ela é revelada só nesse caso e trocada logo em seguida, derrubando a sessão.

### J7. Reiniciar
1. Técnico com `reiniciar` clica "Reiniciar" em uma máquina online e confirma.
2. Funcionário vê o aviso "A TI vai reiniciar seu computador em 2 minutos. Salve seu trabalho."
3. A máquina reinicia; conexão volta a `online` quando reconecta.
- Máquina offline → botão desabilitado (reinício não fica pendente).

### J8. Instalar e remover aplicativos
1. Técnico com `gerenciar_catalogo` mantém o catálogo de aplicativos aprovados; toda alteração é auditada.
2. Técnico com `instalar_apps` escolhe um aplicativo do catálogo e o alvo: uma máquina, várias ou um grupo.
3. Confirma; o sistema cria uma tarefa com o resultado por máquina: pendente, instalado, já instalado, falhou (com erro).
4. Máquinas offline ficam pendentes e instalam quando reconectarem; a tarefa mostra o progresso geral.
5. Remover segue o mesmo fluxo; app que não está instalado conta como sucesso ("não estava instalado").
- Grupos são criados e mantidos por técnicos com `gerenciar_grupos` (ex.: por setor).

### J9. Administrar técnicos
1. Administrador cadastra o técnico pelo usuário do domínio, marca as permissões e gera o código de ativação (aleatório, 16 caracteres, guardado só como hash, uso único, válido por 24 h, inválido após 5 tentativas erradas), entregue ao técnico por fora do sistema.
2. O técnico faz o primeiro acesso (J1).
3. Desativar a conta encerra as sessões ativas do técnico, inclusive suporte remoto em andamento.
- O sistema não permite remover nem desativar o último administrador.
- Criar, alterar permissões, desativar e redefinir 2FA são auditados.

### J10. Consultar auditoria
1. Técnico com `ver_auditoria` consulta o log com filtros por técnico (ou "sistema"), máquina, tipo de ação, período e estado da ação.
2. O log não pode ser editado nem apagado pela aplicação.

## 2. Plano técnico

Base: pesquisa no código do MeshCentral 1.3.1 (master de 04/11/2026). Itens marcados **[validar]** dependem dos testes de conceito (tarefas 2 a 4).

### 2.1 Componentes (um servidor Linux, Docker Compose)
| Componente | Papel |
|---|---|
| Proxy reverso (Caddy) | TLS/HTTPS com certificado do domínio; `gtviewer.<dominio>` → gt_viewer, `mesh.<dominio>` → MeshCentral. Única porta exposta: 443. Logs sem query string; `Referrer-Policy: no-referrer`. |
| gt_viewer | ASP.NET Core (.NET 10 LTS), Razor Pages renderizadas no servidor. Interface dos técnicos + serviços em segundo plano. `ForwardedHeadersMiddleware` configurado confiando exclusivamente no IP do contêiner do Caddy (para rate limit e bloqueio por IP real). |
| MeshCentral | Motor de comunicação com os agentes, **versão fixada** no Compose. Atualização manual, depois de passar os testes de contrato da tarefa 8. |
| PostgreSQL | Banco do gt_viewer (e do MeshCentral, em banco separado **[validar]**). |
| Agente MeshCentral | Serviço Windows (SYSTEM) em cada máquina; conecta ao servidor antes do login do usuário. |

Bibliotecas (todas gratuitas, licença MIT/Apache): ASP.NET Core Identity (2FA/TOTP, travamento, hash de senha da conta de emergência), EF Core + Npgsql, System.DirectoryServices.Protocols (LDAPS), ClosedXML (leitura do .xlsx).

**Exposição na internet (Caddy)** — lista exata fechada na tarefa 4 e testada na tarefa 17:
- Públicos: em `mesh.<dominio>`, só os caminhos dos agentes (`agent.ashx`, `meshrelay.ashx` e os que a tarefa 4 mostrar necessários); em `gtviewer.<dominio>`, só os endereços de token que os scripts das máquinas chamam (`/agente/*`).
- Só da rede interna: interface do gt_viewer; em `mesh.<dominio>`, para os técnicos só as páginas estritamente necessárias ao iframe de tela remota (definidas na tarefa 4), ficando o painel completo do MeshCentral e `control.ashx` restritos ao administrador da plataforma / conta de serviço (decisão do gestor: técnicos acessam só de dentro da empresa).
- Os contêineres não têm saída para a internet (WAN), exceto o Caddy para renovar o certificado; o firewall do host permite ao `gt_viewer` acessar o AD na rede interna (LDAPS/636) e ao `meshcentral` consultar o `certUrl` no Caddy.

### 2.2 Integração com o MeshCentral
- Não há API REST: o gt_viewer mantém **uma conexão WebSocket persistente** com `control.ashx`, autenticada com uma conta de serviço do MeshCentral, com reconexão automática. Direitos mínimos dessa conta definidos na tarefa 4 **[validar]**; como ela precisa criar usuários espelho e conceder direitos, na prática é administradora do MeshCentral.
- **Risco aceito:** quem comprometer o servidor do gt_viewer (conta de serviço ou chave de login) controla todas as máquinas. Mitigações: interface e MeshCentral fora da internet, scripts fixos, segredos só no backend, servidor dedicado e atualizado. Mensagens JSON `{action, responseid}`, seguindo o protocolo do `meshctrl.js`.
- Toda chamada ao MeshCentral passa por uma única classe `MeshCentralClient` (camada que permite trocar a ferramenta depois), coberta por testes de contrato.
- **Eventos:** a mesma conexão recebe `nodeconnect`/`changenode` em tempo real; é isso que dispara as ações pendentes. Ao reconectar, sincronização completa imediata.
- **Comandos:** ação `runcommands` com PowerShell como SYSTEM.
  - Só **scripts fixos, versionados no repositório** (bloquear, desbloquear, garantir/trocar/redefinir conta de resgate, verificar bloqueio, reiniciar, instalar, remover, detectar); nunca texto livre. Parâmetros validados por tipo e escapados.
  - Todo script é idempotente e termina imprimindo `GTV-RESULT=<valor>` (ex.: `OK`, `JA_INSTALADO`, `NAO_INSTALADO`, `BLOQUEADA`, `DESBLOQUEADA`, `OCUPADO`) e `GTV-EXIT=<código>` (`0` = sucesso), que o gt_viewer lê (o MeshCentral não devolve código de saída).
  - Segredos e downloads nunca vão no texto do comando: o script busca o valor num endereço HTTPS do gt_viewer (`/agente/*`) usando um token aleatório de 256 bits (guardado só como hash), enviado no cabeçalho `Authorization` (nunca na URL), atrelado àquela `Acao` e só emitido para o agente aprovado da máquina. Para **senha de resgate**, o token é de **uso único** e válido por **5 minutos**; para **download de instalador**, o token vale pelos **30 minutos** da ação e permite múltiplas requisições/`Range` (para retomada em links lentos). O limite de requisições por IP em `/agente/*` aplica-se apenas a tentativas com token inválido (não bloqueando máquinas legítimas atrás do mesmo NAT corporativo).
- **Avisos ao usuário:** ação `toast` (ex.: aviso de reinício).
- **Configuração obrigatória do MeshCentral:** `tlsoffload:true`, `trustedProxy` apontando para o Caddy, `aliasPort:443` e `certUrl` apontando para o HTTPS do Caddy (com recarga na renovação do certificado, para que os agentes não recusem a conexão com `"Bad web cert hash"`); `agentDownloads.enabled:false` e `selfUpdate:false`; WebRTC desligado (sem STUN externo; o tráfego de tela passa pelo servidor); `allowLoginToken:true`, `allowFraming:true`, `allowedFramingOrigins:["https://gtviewer.<dominio>"]`; administrador com 2FA.
- **Chave de login do MeshCentral:** segredo interno do MeshCentral que o gt_viewer usa para gerar tokens de tela remota; é a única exceção à invariante 4 e fica só no backend, como segredo do Docker.

### 2.3 Inventário e identidade da máquina
- O número de série vem de `hardware.identifiers.bios_serial`; modelo de `product_name`; fabricante de `chassis_manufacturer` (ou `board_vendor`) **[validar]**; SO de `osdesc`; usuários logados de `users`; antivírus de `av` (Central de Segurança do Windows) **[validar]**; online = `conn & 1`.
- Tabela `Maquina` no gt_viewer guarda a ligação **número de série → nodeId atual**, flag `SerieManual` (quando definida manualmente em J2, não é sobrescrita pelo `bios_serial`), dados do agente candidato em conflito ("Aguardando aprovação"), o estado de bloqueio e uma cópia dos campos acima, atualizada em cada evento e por sincronização completa a cada 15 minutos.
- Tabela `HistoricoUsuarioMaquina` registra cada entrada/saída de usuário observada em `users` (máquina, usuário, início e fim), retida por 2 anos (invariante 12).
- Reinstalação e conflito de série: regras de J2. Ações pendentes, histórico e estado de bloqueio ficam ligados à `Maquina`, não ao nodeId.
- Instalador do agente: baixado pelo gt_viewer (que repassa `/meshagents?id=4&meshid=...`), só por técnico com `cadastrar_maquinas`; cada download é auditado. Máquina nova fora do contrato aparece na conferência (J4).

### 2.4 Fila de ações
- Tabela `Acao` (exclusiva para ações em uma máquina): tipo, máquina, ator (técnico ou "sistema"), parâmetros (nunca segredos), estado, tentativas, prioridade, resultado (`GTV-RESULT`).
- Serviço em segundo plano (`BackgroundService`, com exclusão mútua por `Maquina` via `FOR UPDATE SKIP LOCKED` no PostgreSQL): ao receber `nodeconnect` (ou a cada 5 min para as online), envia as ações pendentes daquela máquina **uma por vez**, por prioridade e depois por ordem de criação, **somente se não houver outra ação da mesma máquina em `enviada` ou `resultado desconhecido`**. Prioridade: **garantir/redefinir/trocar senha de resgate > bloquear/desbloquear > instalar/remover**.
- Timeout por tipo (bloqueio/senha: 2 min; instalação: 30 min). Sem `GTV-EXIT` no prazo, ou máquina desconectada com a ação em andamento (vale na hora, sem esperar o timeout) → `resultado desconhecido`. Na subida do `gt_viewer` (para ações órfãs em `enviada`), no `nodeconnect` e no ciclo de 5 min (para máquinas online), o serviço reconcilia ações em `resultado desconhecido`: para senha de resgate, reaplica o script idempotente; para bloqueio/desbloqueio/instalação/remoção, roda o script de verificação do estado real e conclui como `executada` ou reenvia (em instalação/remoção, se o processo `msiexec`/instalador anterior ainda estiver rodando no Windows, retorna `GTV-RESULT=OCUPADO` e aguarda em ciclos de 30 s, como "already busy").
- Limite conhecido: o agente roda um comando por vez, então um bloqueio pode esperar uma instalação em andamento terminar (até 30 min).
- `GTV-EXIT≠0` ou reenvio após `resultado desconhecido` → conta tentativa, até 3; depois `falhou`. Resposta "already busy" / `OCUPADO` → nova tentativa em 30 s, sem contar tentativa, por até 1 h; depois `falhou`.
- Ações de grupo (instalação) criam uma `Tarefa` com uma `Acao` por máquina; a tela da tarefa agrega os estados.
- Reinício e suporte remoto criam `Acao` (para estado na máquina e auditoria na mesma transação), mas não entram na fila e só são aceitos com a máquina online. Ações administrativas e síncronas (login, planilha, técnicos, catálogo, grupos, revelar senha, J2 manual) gravam direto em `Auditoria` na mesma transação, sem criar `Acao`.

### 2.5 Bloqueio e conta de resgate (scripts PowerShell como SYSTEM)
- **Conta e senha de resgate (`garantir`, `trocar` e `redefinir conta de resgate`):** senha aleatória de 20 caracteres, gerada pelo gt_viewer e guardada cifrada (ASP.NET Data Protection). Todas usam um único fluxo idempotente: (1) grava a senha nova como **candidata**; (2) o script busca a candidata pelo endereço de uso único (`/agente/*`), cria ou garante `gtv-resgate` no grupo Administradores, habilitada, desbloqueada (`Unlock-LocalUser`), com `PasswordNeverExpires = true` e `UserMayNotChangePassword = true`, define a senha candidata e, se a senha estava marcada como **"revelada"**, encerra imediatamente quaisquer sessões abertas da `gtv-resgate`; (3) com `GTV-EXIT=0`, a candidata vira a atual. Até a confirmação, tanto a senha atual quanto a candidata ficam disponíveis para revelação. **Não se inicia nova troca enquanto houver candidata não resolvida.** Em `resultado desconhecido`, como o agente roda como `SYSTEM`, basta reexecutar o mesmo script idempotente que aplica a senha candidata. Se terminar em `falhou` (após 3 tentativas), a candidata é descartada e o sistema agenda **redefinir conta de resgate** (que recria/garante a conta com uma nova senha candidata). Se a senha resolvida ainda estiver marcada "revelada", uma nova troca é agendada em seguida.
- **Bloquear:**
  1. Verifica que `gtv-resgate` existe, está habilitada, desbloqueada, com `PasswordNeverExpires` e que a senha atual foi confirmada no banco; senão sai com erro sem alterar nada.
  2. Se o marcador de bloqueio **não** existir: salva a configuração atual de `SeInteractiveLogonRight`, `SeDenyInteractiveLogonRight`, `SeRemoteInteractiveLogonRight`, `SeDenyRemoteInteractiveLogonRight` e `LegalNoticeCaption`/`LegalNoticeText` em arquivo protegido e cria o marcador. Se já existir, não sobrescreve.
  3. Restringe o direito de logon local apenas à `gtv-resgate` (via `secedit`) e nega logon interativo/remoto às demais contas.
  4. Define a mensagem de login (`LegalNoticeCaption`/`LegalNoticeText`).
  5. Cria/atualiza uma tarefa agendada local obrigatória que reaplica o bloqueio **a cada inicialização e a cada 5 minutos (ou evento de aplicação de GPO)** enquanto existir o marcador (impedindo que a atualização periódica de GPO no domínio a cada ~90 min desfaça o bloqueio com a máquina ligada).
  6. Encerra as sessões abertas (`logoff`).
  - Ao reconectar com estado `bloqueada`, o gt_viewer roda a verificação (que também encerra eventual sessão aberta da `gtv-resgate`) e reaplica se necessário.
- **Desbloquear:** restaura todas as configurações e chaves salvas (sem cópia salva → restaura o padrão do Windows), remove mensagem, marcador e tarefa agendada e encerra sessões abertas da `gtv-resgate`.

### 2.6 Suporte remoto
- Cada técnico tem um usuário espelho no MeshCentral, criado pelo gt_viewer, **sem senha** e sem direito a nenhum dispositivo por padrão.
- "Acessar tela":
  1. O gt_viewer verifica permissão, conexão, existência de usuário logado em `Maquina.users` e cria a trava atômica (J5), registrando o usuário que estava logado.
  2. Concede ao usuário espelho, **só naquela máquina**, apenas os direitos "ver e controlar a tela" (bitmask mínimo definido na tarefa 4; sem terminal, arquivos ou comandos).
  3. Gera, no clique, um token de login válido por **2 minutos** (cifrado com a chave de login, no backend) e abre um iframe com `?login=<token>&gotonode=<nodeId>&viewmode=10&hide=31` (**`viewmode=10` vs. `viewmode=11` [validar na tarefa 4]**).
  4. Ao encerrar, na recusa/sem resposta, na falta de renovação da trava, aos 60 min **ou ao receber evento `changenode`/sessão indicando logoff, bloqueio de tela ou troca do usuário que consentiu**: **derruba ativamente a sessão/túnel de tela** (desativando/encerrando as sessões do usuário espelho no MeshCentral) e revoga o direito (com nova tentativa até confirmar). Como derrubar o túnel ativo e como ler o evento de desfecho do consentimento **[validar na tarefa 4]**.
  5. Rotina de reconciliação na subida do serviço e a cada 5 min revoga qualquer direito/sessão de usuário espelho sem trava ativa.
- Consentimento no domínio do MeshCentral: `userConsentFlags = 8 + 16 + 32 + 64` (pedir permissão para tela, terminal e arquivos + barra de sessão ativa), `consentTimeout: 60`, `autoAcceptOnTimeout: false`, sem nenhuma opção `autoAcceptIfNoUser`/`autoAcceptIfLocked`. Textos do pedido em português via `consentMessages`.
- Início e fim da sessão são registrados no gt_viewer (`Acao` + `Auditoria`); detalhes ficam também no log do MeshCentral.

### 2.7 Instalação de aplicativos
- Catálogo próprio (`AppCatalogo`): cada aplicativo tem instalador (MSI/EXE, até 500 MB) **hospedado no próprio servidor**, hash SHA-256 (calculado e gravado pelo `gt_viewer` no upload), **nome do fabricante esperado no certificado digital (`Subject`/Publisher)**, parâmetros de instalação silenciosa, comando de remoção e regra de detecção (chave de registro ou arquivo). Instaladores sem assinatura digital ficam fora do catálogo na v1 (instalados via suporte remoto quando necessário).
- O script no Windows baixa o instalador pelo endereço `/agente/*` (com token válido pelos 30 min da ação e suporte a retomada), confere o **hash SHA-256**, valida a **assinatura digital Authenticode (`Get-AuthenticodeSignature`)** e confere se o **fabricante do certificado corresponde ao esperado no catálogo** antes de executar. Detecção antes de instalar → `GTV-RESULT=JA_INSTALADO`; depois de instalar → confirma o sucesso (`GTV-RESULT=OK`).
- Sem winget/Chocolatey (ver seção 3).

### 2.8 Autenticação, permissões e auditoria
- ASP.NET Core Identity para usuários, TOTP (com bloqueio de reuso do código), travamento e hash da conta de emergência e dos códigos de ativação; validação de senha dos técnicos por bind LDAPS no AD, com cadeia do certificado validada e usuário escapado no filtro LDAP. Segredo TOTP cifrado no banco.
- Limite de tentativas por IP e atraso progressivo por usuário+IP (middleware de rate limiting do ASP.NET Core precedido por `ForwardedHeadersMiddleware`), aplicados antes do bind no AD; tempo mínimo de resposta igual para todos os casos. Tentativas do código de ativação só contam depois de a senha do AD ser validada.
- Comandos de servidor (CLI do próprio gt_viewer): criar primeiro administrador; habilitar conta de emergência por 24 h (ambos gravam em `Auditoria` na mesma transação).
- Permissões: flags por técnico (lista em "Convenções"), verificadas no servidor em cada ação.
- Auditoria: tabela `Auditoria` só de inserção. O usuário do banco usado pela aplicação tem apenas `INSERT` e `SELECT` nela (garantido no PostgreSQL). Toda ação administrativa/síncrona e cada criação ou mudança de estado de uma `Acao` gera uma nova linha em `Auditoria`, na mesma transação. Nunca contém segredos.
- Desativar técnico: invalida sessões e revoga direitos/sessões do usuário espelho no MeshCentral (encerra suporte remoto em andamento).

### 2.9 Planilha da locadora
- ClosedXML lê a primeira aba; regras de J4. Nomes de cabeçalho aceitos definidos com a planilha de exemplo **[validar]**.
- Cada importação fica guardada (`ContratoImportacao` + `ContratoSerial`); a mais recente confirmada é a vigente.

### 2.10 Segredos e operação
- Chave de login do MeshCentral, senha da conta de serviço, credenciais do banco e do AD: segredos do Docker, nunca no repositório.
- Backup diário cifrado do PostgreSQL e da pasta de dados do MeshCentral.
- Chave do Data Protection (que decifra senhas de resgate e segredos TOTP) em volume próprio, com **backup diário separado** do banco, cifrado e guardado pelo gestor de infra (diário porque o Data Protection cria chaves novas periodicamente). Sem ela, as senhas de resgate não podem ser lidas.
- Retenção de auditoria (`Auditoria`, `Acao`) e histórico de usuários por máquina (`HistoricoUsuarioMaquina`): 2 anos; rotina diária remove o que passar disso (única exclusão permitida em `Auditoria`, feita por usuário de banco próprio da rotina, não pelo da aplicação).

## 3. Alternativas e tradeoffs

| Decisão | Alternativa descartada | Por quê |
|---|---|---|
| Solução própria sobre MeshCentral | Microsoft Intune / M365 Business Premium | Custo de licença inviável para ~2700 dispositivos. |
| MeshCentral como motor | Agente próprio do zero | Meses de trabalho, risco de segurança alto e o agente precisaria de assinatura de código; o MeshCentral já resolve conexão, tela remota, comandos e inventário (Apache 2.0). |
| MeshCentral | Tactical RMM | Licença não é de código aberto (uso interno permitido, mas restrita) e ele já usa o MeshCentral por baixo, acrescentando Django + Go sem ganho para a v1. |
| MeshCentral | Fleet | Não tem acesso remoto à tela; parte dos recursos de gerenciamento é paga. |
| Técnicos só no gt_viewer | Técnicos usando o painel do MeshCentral | Mais simples, mas contornaria as permissões, a auditoria e a regra de consentimento do gt_viewer. |
| Tela remota em iframe com token | Cliente de tela remota próprio (via relay do MeshCentral) | Muito mais trabalho; o MeshCentral foi feito para ser embutido dessa forma. |
| Bloqueio por direito de logon local + tarefa local | Desativar a conta no AD | Não funciona com a máquina fora da rede (credenciais em cache) nem para contas locais. |
| Bloqueio por direito de logon local | Só travar a tela | O usuário destrava com a própria senha. |
| Bloqueio por software | Forçar recuperação do BitLocker | Mais forte, mas o BitLocker não está ativo; fica para fase futura. |
| Catálogo com instaladores no próprio servidor | winget / Chocolatey | winget é instável rodando como SYSTEM; repositórios públicos tiram da TI o controle do que é instalado. |
| Fila de ações no gt_viewer | Depender do MeshCentral | O MeshCentral não enfileira comandos para máquinas offline. |
| Tabela `Maquina` no banco | Inventário só em memória | A tabela é necessária para série → agente, estado de bloqueio e filtros; custo baixo para 700 máquinas. |
| Consentimento sempre obrigatório | Acesso sem pedido quando ninguém está logado | O funcionário pode estar no meio de um trabalho importante; sem exceção, não há brecha de LGPD nem caso de login no meio da sessão. |
| C# / ASP.NET Core | Node.js | Node reaproveitaria o `meshctrl.js` diretamente, mas portar o protocolo é trabalho pequeno; C# tem tipagem forte, boas bibliotecas para LDAP, TOTP e Excel, e é a stack que a equipe domina. |
| C# / ASP.NET Core | Python (Django/FastAPI) | Rápido de construir, mas sem vantagem real aqui; desempenho não é gargalo (700 máquinas, carga dominada por rede). |
| Razor Pages (renderizado no servidor) | SPA (React) ou Blazor | Menos código e menos peças para um sistema interno com poucos usuários. |
| ASP.NET Core Identity | TOTP e travamento escritos à mão | Já traz 2FA, travamento e hash de senha testados. |
| PostgreSQL | SQL Server | Licença paga fora da edição Express, que tem limites. |
| PostgreSQL | SQLite | Não permite garantir no banco que a auditoria seja só de inserção (sem controle de permissão por usuário). |
| Login via AD (LDAPS) + autenticador | Contas próprias do gt_viewer | Pedido do gestor; técnicos usam a mesma senha do domínio e contas desativadas no AD perdem acesso em até 15 minutos. |
| Servidor próprio | VPS na nuvem | Decisão do gestor de infra. |

## 4. Plano de implementação

Pré-requisitos de ambiente (fornecidos pela infra, conforme a tarefa): duas VMs Windows 10/11 de teste, uma em grupo de trabalho e outra no domínio (tarefa 2); acesso LDAPS a um AD (tarefa 6); uma planilha da locadora de exemplo (tarefa 15); servidor e domínio definitivos (tarefa 17). As tarefas 1, 5 e 7 rodam na máquina de desenvolvimento.

Testes automatizados: xUnit, rodando contra o PostgreSQL do Docker Compose. Cada tarefa só é dada como pronta quando sua verificação passa.

| # | Tarefa | Como verificar |
|---|---|---|
| 1 | **Esqueleto:** repositório Git, solução .NET 10 (`GtViewer.Web`, `GtViewer.Tests`), Docker Compose com PostgreSQL, MeshCentral (versão fixada) e Caddy; contêineres sem saída para a internet (WAN), mantendo acesso à rede interna (AD LDAPS/636 e Caddy interno). | `docker compose up` sobe tudo; a página inicial responde; `dotnet test` passa. |
| 2 | **Teste de conceito — inventário e comandos:** MeshCentral com a configuração da seção 2.2 (incluindo `tlsoffload`, `certUrl`, `aliasPort: 443` e `trustedProxy` atrás do Caddy); agente nas duas VMs Windows; programa de console em C# que conecta no `control.ashx`, lista máquinas, lê sysinfo, executa script fixo com `GTV-RESULT` e `GTV-EXIT` e recebe `nodeconnect`. | Agentes conectam pelo HTTPS do Caddy sem erro de certificado; série, modelo, fabricante, SO e antivírus corretos nas duas VMs; `GTV-RESULT` e `GTV-EXIT` lidos; evento recebido ao religar uma VM; captura de tráfego do servidor e de uma VM sem nenhum destino fora da empresa. Campos confirmados registrados em `poc.md`. |
| 3 | **Teste de conceito — bloqueio:** scripts de bloquear/desbloquear/conta de resgate (seção 2.5) nas duas VMs. | `gtv-resgate` criada com `PasswordNeverExpires` e desbloqueada; só `gtv-resgate` entra; mensagem aparece; sessões encerradas; bloqueio aplicado duas vezes seguidas e depois desbloqueio restaura todas as políticas e chaves originais; bloqueio recusado sem conta de resgate; continua bloqueada após reiniciar e após `gpupdate /force` na VM do domínio ligada (com a tarefa agendada periódica); rede funciona na tela de login. |
| 4 | **Teste de conceito — tela remota:** iframe com token de 2 min (`viewmode=10` vs. `11` validado), usuário espelho com bitmask mínimo numa única máquina, consentimento, caminhos públicos e internos do Caddy. | Pedido aparece ao abrir o iframe e respeita 60 s; recusa não transmite nada; sem usuário logado não abre; logoff ou bloqueio de tela (`Win+L`) do usuário no Windows encerra imediatamente a sessão; barra de sessão visível; token usado direto em `mesh.<dominio>` não dá acesso a outra máquina, terminal, arquivos nem painel; token expirado é recusado; painel e `control.ashx` inacessíveis de fora e restritos na rede interna. Se 2, 3 ou 4 falharem, a seção 2 é revista antes de continuar. Também: túnel aberto cai ativamente ao revogar e aos 60 min; recusa e sem resposta são detectados pelo gt_viewer; código exato de `viewmode`, direitos mínimos da conta de serviço e lista exata de caminhos públicos/internos registrados em `poc.md`. |
| 5 | **Banco:** modelo EF Core e migrações (`Tecnico`, `Maquina`, `HistoricoUsuarioMaquina`, `Grupo`, `Acao`, `Tarefa`, `AppCatalogo`, `ContratoImportacao`, `ContratoSerial`, `Auditoria`, `TravaSessao`, `AlertaEmergencia`); usuário do banco da aplicação só com `INSERT`/`SELECT` em `Auditoria` e usuário de banco próprio da rotina de retenção (único com `DELETE` em `Auditoria`). | Teste: `UPDATE` e `DELETE` em `Auditoria` falham com o usuário da aplicação; `DELETE` funciona apenas com o usuário da rotina de retenção. |
| 6 | **Login (J1):** `ForwardedHeadersMiddleware` (confiando só no Caddy), Identity + LDAPS, código de ativação, TOTP sem reuso, travamento por conta e por IP real, sessão de 8 h com reconferência no AD, conta de emergência com alerta persistido. | Testes com LDAP simulado (senha certa/errada, conta inativa, desativada durante a sessão, código errado ×5, código reutilizado, primeiro acesso sem código de ativação); teste manual contra o AD real. Também: `X-Forwarded-For` vindo do Caddy isola o bloqueio por IP real do cliente (sem travar outros IPs); usuário não cadastrado não chega ao AD; falhas repetidas aumentam o atraso sem travar a conta; tempo de resposta igual para usuário cadastrado e não cadastrado; código de ativação só conta tentativas com a senha do AD correta; certificado LDAPS inválido é recusado; AD inacessível na reconferência mantém a sessão; sessão expira em 8 h; código de ativação expirado, reutilizado ou após 5 erros é recusado; conta de emergência só entra habilitada pelo comando, tem só as 4 permissões, expira em 24 h derrubando a sessão, gera alerta e não pode reconhecê-lo; comando cria o primeiro administrador. |
| 7 | **Técnicos e permissões (J9):** cadastro por usuário do domínio, flags, código de ativação, proteção do último administrador, desativação. | Mecanismo de permissões testado com uma ação de teste (sem a flag → 403); cada tarefa seguinte inclui o teste de 403 das suas ações; não é possível remover/desativar o último administrador; técnico desativado perde a sessão; ações de J9 geram auditoria direto em `Auditoria`. |
| 8 | **`MeshCentralClient`:** conexão WebSocket persistente, reconexão, pedido/resposta por `responseid`, eventos, sincronização completa ao reconectar; aviso de conexão caída na interface. | Testes de contrato contra o MeshCentral da versão fixada; derrubar e subir o MeshCentral reconecta e sincroniza; chamadas simultâneas recebem as respostas certas; com o MeshCentral fora, a interface mostra o aviso e recusa reinício/tela. |
| 9 | **Inventário (J2, J3):** sincronização por eventos e a cada 15 min; histórico de usuários (`HistoricoUsuarioMaquina`); deduplicação e conflito de série; "sem série" e correção manual (`SerieManual`); estados; antivírus; filtros e busca; download do instalador auditado; garantia da conta de resgate. | Testes de unidade: série nova, série conhecida (vai para aprovação e não recebe nenhuma ação nem token), aprovação com e sem manter bloqueio (exige permissão `bloquear` para limpar ou alterar bloqueio, justificativa exigida para limpar bloqueio, `garantir conta de resgate` agendada com senha nova, ações de bloqueio/senha do agente antigo canceladas), rejeição, ambos online (não aprova), genérica, série manual preservada nas sincronizações seguintes, união de registros com ações pendentes, gravação em `HistoricoUsuarioMaquina` ao mudar `users`; na VM: reinstalar não duplica e mantém histórico; `gtv-resgate` existe com `PasswordNeverExpires` e a senha guardada funciona. |
| 10 | **Fila de ações (2.4):** exclusão mútua por `Maquina`, prioridade (`garantir/redefinir/trocar senha de resgate > bloquear/desbloquear > instalar/remover`), timeout, reconciliação de `resultado desconhecido` (na subida, no `nodeconnect` e a cada 5 min para máquinas online), leitura de `GTV-RESULT` e `GTV-EXIT`, tentativas, auditoria na mesma transação. | Testes de unidade das transições, exclusão mútua (nenhuma ação pendente é enviada enquanto houver outra `enviada` ou `resultado desconhecido` na máquina), prioridade, "already busy"/`OCUPADO` (falha após 1 h), reconciliação de `resultado desconhecido` com máquina online e limite de 3 reenvios; com a gravação de auditoria falhando, nada é enviado; ação criada com a VM desligada executa ao ligar; script interrompido no meio vira `resultado desconhecido` e é concluído na reconexão ou ciclo de 5 min. |
| 11 | **Bloquear/desbloquear e senha de resgate (J6, 2.5).** | Na VM: bloqueio pendente aplicado ao reconectar; desbloquear com bloqueio pendente cancela; revelar disponível em `bloqueada`, `desbloqueio pendente`, `falha` e `bloqueio pendente` (já enviada/`resultado desconhecido`), exigindo offline há >5 min e novo código TOTP, e auditado; senha revelada trocada na conexão seguinte já encerrando sessão aberta da `gtv-resgate` (mesmo se a máquina seguir `bloqueada`); queda no meio da troca mantém candidata e atual utilizáveis e reaplica a candidata de forma idempotente na reconciliação; senha não aparece em `Acao`, auditoria nem log do MeshCentral. Também: desbloquear com bloqueio `enviada` aplica em seguida; desbloquear a partir de `falha`; nova troca de senha recusada com candidata pendente; token de `/agente/*` de senha reutilizado ou expirado é recusado e não aparece em nenhum log; troca de senha em `falhou` dispara `redefinir conta de resgate`; desbloqueio encerra sessão aberta da `gtv-resgate`; revelar indisponível com o MeshCentral caído; auditoria indisponível → nada acontece. |
| 12 | **Reiniciar (J7).** | Aviso aparece e a VM reinicia após 2 min; botão indisponível com a VM offline. Auditoria indisponível → nada acontece. |
| 13 | **Suporte remoto (J5, 2.6).** | Repetir a verificação da tarefa 4 pela interface real; segundo técnico barrado (inclusive com cliques simultâneos); logoff ou bloqueio de tela do funcionário no Windows encerra a sessão na hora; fechar a aba sem encerrar → sessão derrubada e direito revogado após expirar a trava (90 s); reiniciar o gt_viewer no meio da sessão → direito/sessão revogados na subida; desativar o técnico encerra a sessão. Também: trava renovada a cada 30 s, expira em 90 s sem renovação e só é liberada após a derrubada e revogação confirmadas; sessão derrubada aos 60 min; auditoria indisponível → nada acontece. |
| 14 | **Catálogo e instalação (J8, 2.7).** | Instalar um MSI assinado (ex.: Firefox MSI ou Chrome Enterprise MSI) em duas VMs, uma desligada: "instalado" e "pendente" → "instalado" ao ligar; reinstalar dá "já instalado" (`GTV-RESULT=JA_INSTALADO`); no Windows, instalador sem assinatura digital, com hash adulterado ou assinado por fabricante diferente do cadastrado no catálogo dá "falhou" sem executar; token de download permite retomada (`Range`) dentro dos 30 min da ação e é recusado após expirar; se o processo instalador anterior ainda estiver rodando após `resultado desconhecido`, aguarda (`OCUPADO`) sem iniciar outro concorrente; rate limit de `/agente/*` só conta tokens inválidos; remoção funciona (incluindo "não estava instalado"); alterações no catálogo auditadas. |
| 15 | **Planilha da locadora (J4).** | Testes com a planilha de exemplo e variações: válida, cabeçalho na linha 3, séries numéricas com zeros à esquerda, sem coluna de série, repetidas, 0 séries, queda de mais de 20%, arquivo corrompido, arquivo acima de 5 MB; as três listas batem com o inventário de teste. Valores com HTML/fórmula aparecem como texto. |
| 16 | **Auditoria e retenção (J10, 2.10).** | Teste que percorre a lista fechada de tipos (Convenções) e confere a linha de cada um em `Auditoria` (com `Acao` apenas nas ações de máquina), sem segredos; filtros funcionam; rotina diária de retenção (rodando com seu usuário de banco próprio) remove registros de `Auditoria`, `HistoricoUsuarioMaquina` e `Acao` com mais de 2 anos e preserva os mais recentes. |
| 17 | **Implantação no servidor da empresa:** domínio e TLS, segredos, caminhos públicos e internos do Caddy, backups (banco, MeshCentral e chave do Data Protection separada). | Agente numa máquina fora da rede da empresa conecta pela porta 443; painel do MeshCentral inacessível de fora; restauração completa (incluindo chave do Data Protection) testada numa VM limpa e uma senha de resgate lida após a restauração. Também: rotas públicas exatamente as da lista (2.1), interface do gt_viewer inacessível de fora; logs do Caddy sem query string e cabeçalho `Referrer-Policy: no-referrer` presente; restauração testada com dados cifrados depois de uma troca de chave do Data Protection. |
| 18 | **Piloto:** 10 a 20 máquinas reais de técnicos da TI por 2 semanas; depois instalação gradual nas formatações. | Nenhuma falha de bloqueio/desbloqueio, instalação ou suporte remoto no piloto; problemas encontrados viram correção antes de ampliar. |
