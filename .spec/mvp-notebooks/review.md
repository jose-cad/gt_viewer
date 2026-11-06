# Review — spec.md, rodada 1 (2026-11-05)

5 revisores de contexto zero (simplicidade, clareza, bordas, invariantes, segurança). 40 achados consolidados em 22. Todos conferidos contra o texto da spec.

## CRÍTICO

**C1. Bloqueio pode ficar permanente.** Se o bloqueio for reenviado (reaplicação ao reconectar, "already busy", resposta perdida), o passo 1 salva de novo a configuração já restrita por cima da original; o desbloqueio "restaura" o bloqueio. (bordas)
→ Salvar a configuração original só se o marcador de bloqueio não existir; desbloqueio sem cópia salva restaura o padrão do Windows. Todos os scripts idempotentes.

**C2. Máquina pode ficar sem nenhuma forma de entrar.** Senha de resgate gerada, aplicada e trocada sem protocolo: se a conexão cair entre aplicar e confirmar, a senha guardada difere da real; se a `gtv-resgate` não existir ou estiver desabilitada, o bloqueio restringe o logon a uma conta inutilizável. A chave que cifra as senhas (Data Protection) não entra no backup. A senha viaja dentro do script e tende a parar em `Acao.parametros`, na auditoria (que não pode ser apagada) e no log do MeshCentral. (bordas, invariantes, segurança)
→ Senha nova gravada como "candidata" antes de aplicar e promovida só com `GTV-EXIT=0` (as duas valem até lá). Bloqueio confere que `gtv-resgate` existe, está habilitada e com senha confirmada; senão aborta sem restringir nada. Script busca a senha num endereço HTTPS de uso único em vez de levá-la no texto do comando; parâmetros e auditoria registram só "senha trocada". Chave do Data Protection em backup próprio, separado e cifrado, com restauração testada.

**C3. Token da tela remota pode ser reaproveitado fora do gt_viewer.** O token de 60 min vai na URL do iframe, aparece em logs/histórico/Referer e abre uma sessão real no MeshCentral, utilizável fora do gt_viewer (contorna trava e auditoria). Direitos exatos do usuário espelho não definidos; consentimento cobre só tela, não terminal/arquivos. Se o gt_viewer cair ou o técnico fechar a aba, ninguém revoga o direito; a trava por máquina não é atômica. (segurança, invariantes, bordas, simplicidade)
→ Token gerado no clique e válido por 2 min (só para abrir a sessão); usuário espelho só com "ver e controlar tela" (sem terminal, arquivos, comandos); Caddy sem registrar query string e `Referrer-Policy: no-referrer`; trava atômica no banco com renovação periódica; revogação com nova tentativa e rotina que revoga direitos órfãos na subida e a cada 5 min; sessão termina aos 60 min. Teste: usar o token direto no MeshCentral não dá acesso a nada além da tela daquela máquina.

**C4. Exceção "sem usuário logado" contradiz a regra de consentimento.** J5 libera a tela sem pedido se ninguém estiver logado, mas a invariante 9 e o brief dizem "sempre". Se alguém fizer login durante a sessão, a tela pessoal fica exposta sem pedido. (clareza, invariantes, segurança)
→ **Decisão sua** (ver pergunta 1).

## IMPORTANTE

**I1. Painel do MeshCentral exposto na internet.** `mesh.<dominio>` publica login do administrador e `control.ashx`; a restrição é só convenção. Comprometer o servidor ou a conta de serviço = execução como SYSTEM nas 700 máquinas. (segurança, simplicidade)
→ Caddy libera para a internet só os caminhos dos agentes; painel e `control.ashx` só pela rede interna (ver pergunta 2). Scripts fixos e versionados no código, parâmetros validados, nunca texto livre. Versão do MeshCentral fixada, com teste de contrato do `MeshCentralClient` e rotina de atualização manual.

**I2. Ação "enviada" sem resposta.** Não há timeout, limite de tentativas, tratamento de `GTV-EXIT≠0` nem prioridade; uma instalação longa trava bloqueio e resgate. (bordas, clareza)
→ Timeout por tipo; estado "resultado desconhecido" que reconsulta o estado real (scripts idempotentes); até 3 tentativas; bloqueio/desbloqueio/senha passam na frente de instalações.

**I3. "Sem registro, sem envio" não tem mecanismo.** Ações fora da fila (reiniciar, tela, revelar senha, importar, J9) sem estados; ações do sistema sem técnico. (invariantes)
→ Auditoria e estado da ação gravados na mesma transação antes do envio; lista fechada de tipos auditados, incluindo reset de 2FA e ações do sistema com ator "sistema"; teste: auditoria indisponível → nada é enviado.

**I4. Deduplicação por série pode apagar máquina viva.** Séries clonadas/genéricas ("Default string", "System Serial Number", "0123456789"…) fariam duas máquinas se apagarem; correção de "sem série" não tem fluxo. (bordas, clareza, invariantes)
→ Só remove o nodeId antigo se estiver offline; duas máquinas online com a mesma série → conflito para resolução manual; lista ampliada de séries genéricas; tela de correção manual com permissão própria; ações pendentes e estado de bloqueio pertencem à máquina (série), não ao nodeId.

**I5. Conta desativada no AD continua com acesso por até 8 h.** (bordas, invariantes)
→ Revalidar a conta no AD a cada 15 min; desativada → sessão encerrada.

**I6. Permissões ambíguas.** "Administrador" não definido; revelar senha, baixar instalador, auditoria e grupos sem permissão própria; catálogo misturado com administrar técnicos. (clareza, segurança)
→ Lista fechada de permissões, uma por ação, e "administrador" = quem tem "administrar técnicos". Catálogo separado.

**I7. Estados misturados.** Máquina, bloqueio e ação usam vocabulários diferentes. (clareza)
→ Três eixos definidos: conexão (online/offline/sumida), bloqueio (desbloqueada/bloqueio pendente/bloqueada/desbloqueio pendente/falha) e ação (pendente/enviada/executada/falhou/resultado desconhecido/cancelada).

**I8. Senha de resgate: quando trocar e quem desbloqueia.** (clareza)
→ Revelar só com a máquina offline (online, desbloqueia-se direto); troca na primeira conexão após a revelação; desbloqueio iniciado pelo técnico que está no telefone.

**I9. Instalador trocado no servidor executa como SYSTEM.** Hash fica no mesmo banco; sem verificação de assinatura; upload sem limite. (segurança, simplicidade)
→ Exigir assinatura digital (Authenticode) válida do fabricante além do hash; limite de tamanho/tipo; toda alteração no catálogo auditada; URL de download com token de uso único (mesmo mecanismo da senha).

**I10. Login e conta de emergência.** Sem hash forte, anti-reuso de código, limite por IP; quem souber a senha do AD antes do técnico cadastra o próprio autenticador. (segurança, invariantes, simplicidade)
→ Usar ASP.NET Core Identity (já traz TOTP, travamento, hash forte) com validação de senha no AD; código de ativação de uso único emitido pelo administrador para o primeiro cadastro de 2FA; limite também por IP; conta de emergência sempre disponível, mas cada uso aparece em destaque para todos os administradores.

**I11. Planilha da locadora.** Série numérica perde zeros, cabeçalho fora da linha 1, arquivo gigante, planilha com 0 séries vira lista vigente. (bordas, segurança)
→ Série lida como texto; procura do cabeçalho nas 10 primeiras linhas; limite de 5 MB; confirmação bloqueada com 0 séries e reforçada se a quantidade cair mais de 20%; prévia gravada e confirmada pelo identificador.

**I12. Conexão com o MeshCentral caída.** Estado "online" fica velho, eventos se perdem. (bordas)
→ Aviso na tela com o horário dos dados; sincronização completa ao reconectar; reiniciar e tela remota indisponíveis enquanto a conexão estiver caída.

**I13. Nada verifica que não há tráfego para terceiros.** (invariantes)
→ Bloquear saída para a internet no Compose (exceto renovação de certificado) e testar na tarefa 2.

**I14. Retenção de dados (LGPD).** Usuário logado e auditoria guardados sem prazo. (segurança)
→ **Decisão do gestor** (ver pergunta 3).

## MENOR

- **M1.** Brief diverge da spec em 3 pontos (reinício não fica pendente; permissões individuais em vez de perfis; integração por WebSocket, não REST) → ajustar a redação do brief.
- **M2.** A chave de login do MeshCentral é um segredo interno dele usado pelo gt_viewer → registrar como exceção explícita da invariante 4.
- **M3.** Tarefa agendada do bloqueio pode ser desnecessária → o teste de conceito (tarefa 3) decide; se o bloqueio sobreviver a reinício e `gpupdate` sem ela, sai.
- **M4.** Fila implementada com `BackgroundService` + `FOR UPDATE SKIP LOCKED` no PostgreSQL, sem biblioteca de filas.

## Rejeitado

- **Guardar inventário só em memória** (simplicidade): a tabela é necessária para série → nodeId, estado de bloqueio e filtros; para 700 máquinas o custo é baixo.

## Perguntas
1. Sem usuário logado: (a) remover a exceção — sempre alguém precisa aceitar; máquina sem ninguém logado não recebe acesso à tela (recomendado: mais simples e sem brecha de LGPD); ou (b) manter a exceção e encerrar a sessão se alguém fizer login durante ela.
2. Os técnicos que farão suporte remoto trabalham dentro da rede da empresa (ou com VPN)? Se sim, o painel do MeshCentral fica inacessível pela internet.
3. Por quanto tempo guardar auditoria e histórico de usuário por máquina? Sugestão: 2 anos.

---

# Review — rodada 2 (2026-11-05)

Escopo: seções alteradas após a rodada 1. 3 revisores (bordas, segurança, invariantes/clareza); 24 achados consolidados em 16. Decisões da rodada 1: pergunta 1 → consentimento sempre obrigatório, sem exceção (decisão do usuário). Perguntas 2 e 3 seguem pendentes com o gestor.

## CRÍTICO (corrigidos)
- **Senha candidata sobrescrita** (bordas): nova troca bloqueada enquanto houver candidata não resolvida; verificação testa as duas senhas na máquina.
- **Agente falso assume série de máquina offline e recebe a senha de resgate** (segurança): série conhecida nunca substitui sozinha — vai para "Aguardando aprovação" sem receber ações; aprovação cria senha nova; token de segredo atrelado à ação e à máquina.

## IMPORTANTE (corrigidos)
- Máquina bloqueada reformatada voltava bloqueada → na aprovação o técnico escolhe manter ou limpar (padrão: limpar).
- Primeiro administrador e conta de emergência sem bootstrap/permissões → comandos no servidor; emergência desativada por padrão, habilitada pelo gestor por 24 h, sem `revelar_senha_resgate`, não reconhece o próprio alerta.
- AD fora na reconferência → sessão continua até 8 h, registrado.
- Tentativas de login travando contas do AD → checagem de cadastro e limite por conta antes do bind; limite por IP 10/min.
- Ações fora da fila sem estado/auditoria → todas criam `Acao`; lista fechada de tipos auditados.
- Garantias sem verificação → acrescentadas às tarefas 4, 6, 11, 12, 13, 15 e 17.
- Exposição do Caddy indefinida → lista de rotas públicas/internas (2.1); depende da pergunta 2.
- Conta de serviço é admin de fato → registrado como risco aceito com mitigações; direitos mínimos validados na tarefa 4.
- Túnel de tela não cai ao revogar → sessão derrubada ativamente; detecção de recusa via evento **[validar]**.
- Backup único da chave do Data Protection → backup diário; restauração testada após troca de chave.
- Desbloquear com bloqueio já enviado / a partir de `falha` → regras explícitas em J6.
- Instalação em andamento segura bloqueio → desconexão vira `resultado desconhecido` na hora; espera restante registrada como limite conhecido.
- União de registros → técnico escolhe o registro; senha nova e verificação do bloqueio.
- Revelar senha → só com `bloqueada` + offline + novo código 2FA; limite de admin local declarado.

## MENOR (corrigidos)
- Tarefa 7 dependia de ações futuras → testa o mecanismo; cada tarefa testa seu 403.
- Código de ativação, LDAPS e filtro LDAP → especificados (16 caracteres, hash, 5 tentativas; certificado validado; escape).

---

# Review — rodada 3 (2026-11-06, última)

Escopo: correções da rodada 2. 2 revisores (bordas, segurança); 12 achados consolidados em 11, todos corrigidos.

- **CRÍTICO** — candidata de senha sem saída travava trocas futuras → `falhou` ou nenhuma senha válida descarta a candidata e agenda "redefinir conta de resgate"; aprovação/união descartam a candidata do agente antigo.
- Senha revelada podia continuar válida → marca "revelada" força nova troca após qualquer resolução.
- Revelação com dados velhos do MeshCentral → indisponível com a conexão caída; exige offline há mais de 5 min.
- Ações do agente antigo migravam na aprovação → bloqueio/desbloqueio/senha cancelados; instalações mantidas; manter bloqueio gera ação nova; limpar bloqueio exige justificativa; tela de aprovação com evidências lado a lado.
- Conta de emergência com `administrar_tecnicos` → só inventário, bloquear, reiniciar e suporte remoto; sessão cai ao expirar; fora da reconferência no AD.
- Trava de tela liberada antes da revogação → liberada só após derrubada/revogação confirmadas; expira em 90 s sem renovação.
- Reenvios e "already busy" infinitos → contam no limite de 3; busy limitado a 1 h.
- Token de `/agente/*` → 256 bits, hash, cabeçalho `Authorization`, rate limit; teste "de outra máquina" removido (não verificável).
- Sessão da `gtv-resgate` seguia aberta após desbloqueio → desbloqueio encerra as sessões dela.
- Pré-checagem de login permitia travar contas e revelar técnicos → atraso progressivo por usuário+IP, tempo de resposta constante, código de ativação só conta com senha do AD válida.

Limite de rodadas atingido. Pendências com o gestor: pergunta 2 (rede dos técnicos) e 3 (retenção), ambas bloqueiam só a tarefa 17.

**Respostas do gestor (2026-11-06):** pergunta 2 → técnicos acessam só da rede interna; pergunta 3 → retenção de 2 anos. Spec aprovada.

---

# Revisão pré-implementação — Planos e Ações (2026-11-06)

5 revisores de contexto zero (simplicidade, clareza, bordas/falhas, invariantes, domínio Windows/MeshCentral/segurança) sobre a versão final de `spec.md`, `brief.md` e `invariants.md`. 37 achados brutos consolidados em 18 itens verificados contra a spec.

## CRÍTICO

**C1. Proxy reverso (Caddy) quebra conexão dos agentes e trava logins/rate-limit por IP (`spec.md:25, 110, 131, 133`).**
Com o Caddy terminando o TLS na frente do MeshCentral e do `gt_viewer`, faltam `tlsoffload`, `certUrl`, `aliasPort: 443` e `trustedProxy` na configuração obrigatória do MeshCentral (sem isso, os agentes Windows recusam a conexão com erro `"Bad web cert hash"`, inclusive a cada renovação do certificado pelo Caddy) e falta `ForwardedHeadersMiddleware` no ASP.NET Core (sem isso, o `gt_viewer` enxerga o IP interno do contêiner do Caddy para todos os acessos, travando todos os técnicos após 10 falhas de login em J1 e aplicando o rate limit de `/agente/*` globalmente às 700 máquinas).
→ Incluir `tlsoffload`, `trustedProxy`, `aliasPort: 443` e `certUrl` no MeshCentral e `ForwardedHeadersMiddleware` (confiando só no IP do Caddy) no `gt_viewer`.

**C2. Suporte remoto (J5 / 2.6): `viewmode=10` não abre a tela, consentimento depende do túnel do iframe e logoff/lock do usuário viola a Invariante 9 (`spec.md:57-61, 169-171`, `invariants.md:14`).**
- Na URL do iframe (`spec.md:169`), `viewmode=10` abre a aba de dados gerais do dispositivo (a tela remota no MeshCentral é `viewmode=11`) e `hide=31` oculta a barra de abas, impedindo o técnico de chegar à tela e impedindo o disparo do pedido de consentimento (que no MeshCentral só ocorre quando o túnel `meshrelay.ashx` da aba de desktop tenta conectar dentro do iframe).
- Como o agente roda como `SYSTEM`, se o funcionário que aceitou o acesso fizer logoff, bloquear a tela (`Win+L`) ou trocar de usuário durante os 60 min, o streaming continua exibindo a tela de login ou a sessão de outro usuário sem consentimento, violando a Invariante 9.
- Apenas revogar a permissão do usuário espelho no dispositivo não derruba um WebSocket `meshrelay.ashx` já aberto nem invalida o cookie de sessão criado em `mesh.<dominio>`.
→ Usar `viewmode=11` no iframe; ajustar J5/2.6 para refletir que o prompt de consentimento é disparado pela conexão do iframe; monitorar mudança/saída do usuário logado (`changenode`/sessão Windows) para encerrar imediatamente a sessão remota; ao encerrar/expirar, remover ou desativar o usuário espelho no MeshCentral (derrubando túneis ativos) e restringir no Caddy as rotas internas de `mesh.<dominio>` apenas ao necessário para o embed.

**C3. Conta `gtv-resgate`: senha expira em 42 dias, pode ser travada na tela de login e sessão continua aberta como Admin se a máquina seguir `bloqueada` (`spec.md:69, 75, 152-162`, `invariants.md:13`).**
- A criação da `gtv-resgate` não define `PasswordNeverExpires` (`ADS_UF_DONT_EXPIRE_PASSWD`) nem `UserMayNotChangePassword` ou desbloqueio de lockout; após 42 dias (padrão do Windows), a senha expira, fazendo falhar a checagem do passo 1 do bloqueio ou impedindo o login offline por telefone.
- Quando a senha é revelada por telefone para o funcionário conectar ao Wi-Fi (`spec.md:75`), a spec troca a senha na primeira conexão, mas só encerra as sessões da `gtv-resgate` "ao desbloquear" — deixando o funcionário logado como Administrador local caso a máquina continue `bloqueada`.
→ Definir `PasswordNeverExpires = true` e desbloquear a conta nos scripts de garantia/bloqueio; encerrar obrigatoriamente qualquer sessão aberta da `gtv-resgate` já no script de troca de senha revelada (e na verificação de máquina `bloqueada`).

**C4. Revelar senha de resgate fica bloqueado em estados reais de máquina bloqueada sem internet (`spec.md:68-75, 145`).**
`revelar_senha_resgate` exige estado estritamente `bloqueada`. Porém: (a) se o passo 6 do script de bloqueio (`logoff`) derrubar o Wi-Fi/hotspot do usuário antes de o `GTV-EXIT=0` chegar ao servidor, a máquina fica fisicamente bloqueada no Windows e sem internet, mas em estado `bloqueio pendente` (`resultado desconhecido`); (b) se o técnico clicar em "Desbloquear" com a máquina offline antes de perceber que ela precisa de uma rede Wi-Fi nova, o estado muda para `desbloqueio pendente` (ou `falha`), travando a revelação da senha.
→ Permitir `revelar_senha_resgate` também quando o estado for `bloqueio pendente` (com ação já enviada/`resultado desconhecido`), `desbloqueio pendente` ou `falha`, mantendo as exigências de offline há >5 min e confirmação de TOTP.

**C5. Fila de ações: falta prioridade para criar/redefinir conta de resgate, falta exclusão mútua por máquina e `resultado desconhecido` trava com máquina online (`spec.md:33-35, 144-145, 152-155`).**
- `garantir conta de resgate` e `redefinir conta de resgate` não estão na regra de prioridade (`troca de senha > bloquear/desbloquear > instalar/remover`); ao aprovar máquina reformatada mantendo o bloqueio (ou bloquear máquina recém-cadastrada), `bloquear` pode rodar antes de a conta `gtv-resgate` existir/ter senha confirmada e falhar no passo 1.
- `FOR UPDATE SKIP LOCKED` apenas na tabela `Acao` (sem trava por `Maquina`) permite que um disparo concorrente (`nodeconnect` + ciclo de 5 min) pule uma ação prioritária que já está `enviada` ou em `resultado desconhecido` e envie a próxima ação `pendente` da mesma máquina.
- Ações que caem em `resultado desconhecido` por timeout com a máquina **online** (ou órfãs em `enviada` após reinício do `gt_viewer`) só têm verificação prevista "na próxima conexão", ficando presas enquanto a máquina não desconectar.
→ Colocar `garantir` e `redefinir conta de resgate` na prioridade máxima (junto à troca de senha, usando o mesmo fluxo `candidata → atual`); impedir envio de nova ação de uma `Maquina` enquanto houver outra `enviada` ou `resultado desconhecido` nela; reconciliar ações `resultado desconhecido` (e `enviada` órfãs na subida) também no ciclo periódico de 5 min para máquinas online.

**C6. "Quem baixou o instalador" por agente (`spec.md:34, 140`) é impossível com o instalador de grupo do MeshCentral.**
A tela de aprovação de série repetida exige mostrar "quem baixou o instalador" do agente antigo e do novo (`spec.md:34`), mas o download apenas repassa `/meshagents?id=4&meshid=...` (`spec.md:140`), que é um binário único por grupo (e reutilizado pelos técnicos em várias formatações), sem vínculo com o `nodeId` que se conecta depois.
→ Remover "quem baixou o instalador" da comparação por agente em J2 (mantendo a auditoria geral de downloads do instalador).

**C7. Invariante 12 sem tabela de histórico de usuários nem tarefa de expurgo de 2 anos (`invariants.md:17`, `spec.md:138, 197, 237`).**
A Invariante 12 e a seção 2.10 exigem guardar por 2 anos e remover diariamente (com usuário de banco dedicado) a auditoria e o **histórico de usuários por máquina**, mas `Maquina` guarda só o usuário atual, a Tarefa 5 não inclui tabela de histórico de usuários e nenhuma tarefa da seção 4 implementa ou testa a rotina de expurgo e seu usuário de banco.
→ Adicionar `HistoricoUsuarioMaquina` na seção 2.3/Tarefa 5 (gravado quando `users` mudar) e incluir a rotina diária de retenção de 2 anos (com usuário de banco próprio) nas tarefas 5 e 16.

## IMPORTANTE

**I1. Refresh periódico de GPO no domínio pode sobrescrever o bloqueio sem reboot (`spec.md:156-162, 235`).**
O Windows atualiza políticas de domínio (GPO) em segundo plano a cada ~90 min com a máquina ligada, o que pode sobrescrever `SeInteractiveLogonRight`, enquanto a tarefa agendada prevista em `2.5` roda só na inicialização (e a Tarefa 3 prevê removê-la se `gpupdate /force` na VM de teste não desfizer o bloqueio, o que dará falso-positivo se o AD de teste não tiver GPO configurando esse direito). Além disso, o passo 2 salva só `SeInteractiveLogonRight`, sem salvar o estado original de `SeDenyRemoteInteractiveLogonRight` e `LegalNotice*`.
→ Manter a tarefa agendada obrigatória disparando na inicialização e periodicamente (ou no evento de aplicação de GPO) enquanto existir o marcador, e salvar/restaurar todas as configurações alteradas.

**I2. Permissão em J2 permite que `cadastrar_maquinas` desbloqueie máquina sem permissão `bloquear` (`spec.md:35, 39`, `invariants.md:11`).**
Na aprovação de troca de agente e na união de registros, o técnico com `cadastrar_maquinas` pode escolher limpar o bloqueio (que é inclusive o padrão), mesmo sem ter a permissão `bloquear`. Além disso, na união (`spec.md:39`), fica ambíguo se prevalece a escolha do técnico ou a verificação na máquina.
→ Exigir a permissão `bloquear` para alterar o estado de bloqueio na aprovação/união (sem ela, mantém-se obrigatoriamente o estado vigente da máquina) e definir que a verificação na máquina serve para aplicar o estado decidido no banco.

**I3. Série corrigida manualmente pode ser sobrescrita na sincronização de 15 min (`spec.md:39, 137-138`).**
Quando uma máquina tem `bios_serial` genérico e recebe série manual em J2, a sincronização periódica que lê `hardware.identifiers.bios_serial` não pode sobrescrever a série corrigida.
→ Marcar em `Maquina` quando a série foi definida manualmente, preservando-a nas sincronizações seguintes daquele `nodeId`.

**I4. Validação de Authenticode (`.exe`/`.msi`) no contêiner Linux sem internet e exemplo do 7-Zip (`spec.md:116, 121, 177, 246`).**
O .NET em Linux não possui `WinVerifyTrust` nativo para validar Authenticode de binários PE e pacotes OLE (`.msi`), e o contêiner não tem internet para consultar CRL/OCSP. Além disso, o MSI oficial do 7-Zip (citado como exemplo na Tarefa 14) não possui assinatura digital Authenticode.
→ Duas opções: (a) usar `osslsigncode` no contêiner Linux com validação offline de cadeia (sem OCSP/CRL) e trocar o exemplo da Tarefa 14 por um MSI assinado; ou (b) mais simples: no upload gravar apenas o hash SHA-256 e validar o Authenticode no próprio Windows via `Get-AuthenticodeSignature` no script PowerShell antes de executar.

**I5. Download de instaladores de 500 MB via `/agente/*` com token de uso único de 5 min e rate limit por IP (`spec.md:131, 145, 176-178`).**
Instaladores não são segredos (Invariante 10) e têm timeout de 30 min; usar token de uso estritamente único de 5 min e rate limit por IP derrubará instalações em grupo atrás do mesmo NAT corporativo e impedirá retomada de download em links lentos. Além disso, se a conexão WebSocket oscilar durante um `msiexec`, reenviar a instalação imediatamente na reconexão causará erro `1618` do Windows Installer se o processo anterior ainda estiver rodando.
→ Permitir múltiplos requests/`Range` no token de download de instalador durante os 30 min da `Acao`; aplicar rate limit por IP apenas a falhas de token inválido; no script de verificação após `resultado desconhecido`, checar se o instalador anterior ainda está em execução antes de disparar outro.

**I6. Protocolo `GTV-EXIT` não diferencia resultados válidos distintos (`spec.md:87, 89, 130, 145, 147, 152, 178`).**
A seção 2.2 define apenas `GTV-EXIT=<código>` e a fila trata `GTV-EXIT≠0` como erro com nova tentativa, mas os scripts precisam informar desfechos de sucesso distintos (`instalado` vs. `já instalado`, `removido` vs. `não estava instalado`, estado verificado na reconexão, qual senha funcionou).
→ Padronizar uma linha adicional `GTV-RESULT=<valor>` junto com `GTV-EXIT=0`.

**I7. Tabela `Acao` usada para ações administrativas sem máquina (`spec.md:12, 143, 149`).**
Obrigar login, importação de planilha, cadastro de técnicos e alterações de catálogo/grupos a criar registro na tabela `Acao` (que modela a fila de comandos de máquinas) acopla dois conceitos diferentes, quando gravar diretamente em `Auditoria` na mesma transação do banco já cumpre a Invariante 5.
→ Restringir a tabela `Acao` aos comandos voltados a máquinas (ou tornar `MaquinaId` nulo e especificar os estados síncronos) e gravar todas as ações diretamente em `Auditoria` na mesma transação.

## MENOR / SIMPLIFICAÇÕES

- **M1. Rede Docker `internal` vs. acesso ao AD (`spec.md:121, 182, 233`):** bloquear saída para a internet não pode usar uma rede Docker totalmente isolada que impeça o `gt_viewer` de alcançar o AD interno via LDAPS (porta 636) ou o `meshcentral` de consultar o `certUrl` no Caddy; restringir apenas a saída para a WAN (ex.: regras `DOCKER-USER` no host).
- **M2. Lista fechada de tipos auditados (`spec.md:12`):** acrescentar à lista os eventos de sistema/CLI já previstos no texto: criar primeiro admin (CLI), habilitar/expirar conta de emergência, falha de reconferência do AD, redefinir conta de resgate, verificar/reaplicar bloqueio na reconexão e revogar direitos órfãos de tela remota.
- **M3. Entidades de agentes pendentes e alertas de emergência (`spec.md:27, 34, 237`):** explicitar na Tarefa 5 onde ficam salvos os dados do agente em "Aguardando aprovação" (colunas candidatas em `Maquina` ou tabela `AgentePendente`) e o reconhecimento do alerta de uso da conta de emergência.
- **M4. Simplificação opcional na recuperação da senha de resgate (`spec.md:152`):** em vez de um script que recebe duas senhas e testa login local no Windows após `resultado desconhecido`, basta reexecutar de forma idempotente o script que aplica a senha `candidata` na `gtv-resgate` (já que o agente roda como `SYSTEM`), mantendo a anterior e a candidata disponíveis para revelação até o `GTV-EXIT=0`.

**Decisões e aplicação (2026-11-06):**
- Todos os 18 itens (C1–C7, I1–I7, M1–M4) aprovados e aplicados em `spec.md`.
- **I4 (Assinatura dos instaladores):** opção (a) + validação do fabricante esperado (`Subject`/Publisher cadastrado em `AppCatalogo`) junto ao hash SHA-256 e `Get-AuthenticodeSignature` no script PowerShell do Windows; instaladores sem assinatura ficam fora do catálogo na v1.
- **I7 (Tabela `Acao` vs. `Auditoria`):** opção (a) + `reiniciar` e `acessar tela` permanecem em `Acao` (por serem ações em uma máquina com estado); ações administrativas e síncronas gravam direto em `Auditoria` na mesma transação.
- **M4 (Recuperação de senha de resgate):** opção (a) — reaplicação idempotente da senha `candidata` como `SYSTEM` sem script de teste local de duas senhas.
- **C2 (`viewmode=10` vs. `viewmode=11`):** marcado como `[validar na tarefa 4]` para confirmação prática no PoC.
- `spec.md` mantida com `STATUS: APPROVED (2026-11-06)`.

