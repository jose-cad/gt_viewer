# Invariantes — gt_viewer

Projeto sem código ainda; regras vindas das decisões do brainstorm.

## Regras
1. Custo de licença zero: só software livre/gratuito com licença que permita gerenciar os sistemas da própria empresa. Servidor em Linux.
2. Auto-hospedado no servidor da empresa; nenhum dado de máquina vai para serviço de terceiros (inclui telemetria e relays da ferramenta).
3. Técnicos usam só o gt_viewer. O painel do MeshCentral fica restrito ao administrador da plataforma, com verificação em duas etapas.
4. O gt_viewer fala com o MeshCentral só pela API dele, com conta de serviço própria; nunca acessa o banco ou os internals do MeshCentral. Única exceção: a chave de login do MeshCentral, usada só no backend para gerar tokens de tela remota.
5. Toda ação feita pelo gt_viewer (acessar tela, bloquear, desbloquear, reiniciar, instalar/remover app, revelar senha, importar planilha, cadastrar máquina, alterar técnicos e permissões, ações automáticas) é registrada em log que só cresce (exceto a remoção após 2 anos, regra 12), com: ator (técnico ou "sistema"), alvo, parâmetros, data/hora e estado. Sem registro gravado, a ação não é enviada.
6. Cada técnico entra com a própria conta do domínio (AD via LDAPS, nunca LDAP sem criptografia) mais verificação em duas etapas, e só executa o que suas permissões no gt_viewer autorizam. Nunca conta compartilhada; a única conta local é a de emergência, para AD fora do ar, habilitada só pelo gestor no servidor e por 24 h.
7. O número de série é a identidade da máquina.
8. O desbloqueio é sempre remoto, pelo gt_viewer. O bloqueio preserva uma conta de resgate com senha única por máquina, revelada só com a máquina bloqueada e sem internet (passada por telefone) e trocada na primeira conexão seguinte.
9. Acesso remoto à tela só com permissão de um usuário logado a cada sessão e aviso visível enquanto durar (LGPD). Sem ninguém logado, não há acesso. Sem exceções.
10. Segredos (senhas de resgate, segredos de 2FA, chaves) nunca aparecem em log, auditoria, parâmetros de ação ou texto de comando.
11. Interface do gt_viewer e painel do MeshCentral só acessíveis da rede interna; da internet, só os caminhos usados pelas máquinas.
12. Auditoria e histórico de usuários por máquina são guardados por 2 anos (LGPD) e removidos depois disso.

## Convenções
- Código próprio em C#/ASP.NET Core.
- O MeshCentral fica atrás de uma camada própria no gt_viewer, para poder ser trocado sem reescrever o resto.
- O MeshCentral é a fonte do inventário; o gt_viewer mantém só uma cópia sincronizada (por eventos e a cada 15 min) para listar e filtrar, mais o que só ele tem (série → máquina, permissões, auditoria, fila de ações, planilha da locadora).

## Onde vive no código
(preencher após a implementação)
