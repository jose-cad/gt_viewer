# Brief — gt_viewer: MVP de gerenciamento de notebooks

STATUS: APROVADO (2026-11-05)

## Problema
A empresa precisa gerenciar remotamente ~700 computadores (maioria notebooks, alguns desktops e algumas máquinas Linux) e ~2000 celulares Android. O Microsoft Intune resolveria, mas o custo de licença é inviável. A maior dor são os notebooks: são **alugados**, espalhados por todo o Brasil, e a empresa precisa de controle maior sobre eles do que sobre os celulares, que são próprios.

## Objetivo
Primeira versão do gt_viewer: aplicação web própria, feita do zero em C#/ASP.NET Core, focada em computadores Windows (notebooks e desktops). Por baixo, usa o MeshCentral (código aberto, Apache 2.0) como motor de comunicação com as máquinas, integrado pela API dele (WebSocket) no mesmo modelo em que a empresa já integra a Evolution API.

## Arquitetura em uma frase
Um servidor Linux da empresa, com domínio próprio, roda o gt_viewer e o MeshCentral. Cada computador roda o agente do MeshCentral, que se conecta ao servidor pela internet. Os técnicos usam só o gt_viewer; o painel do MeshCentral fica restrito ao administrador da plataforma.

## Escopo da v1
1. **Inventário:** modelo, fabricante, número de série, SO/versão, usuário logado, último contato, situação do antivírus (ativo e atualizado ou não). O número de série identifica a máquina (reformatar não cria duplicata).
2. **Conferência com o contrato de aluguel:** importar a planilha Excel da locadora e cruzar os números de série com o inventário, apontando máquinas do contrato que nunca apareceram/sumiram e máquinas no inventário fora do contrato.
3. **Suporte remoto:** técnico abre a tela da máquina a partir do gt_viewer, **sempre com permissão do funcionário** a cada acesso e aviso visível durante a sessão.
4. **Ações remotas:** reiniciar; **bloquear acesso** (ninguém consegue fazer login, nem conta de domínio nem conta local) e desbloquear. Bloquear/desbloquear com a máquina offline ficam pendentes e são aplicados quando ela reconectar; reiniciar só com a máquina online. O estado fica visível.
5. **Instalação de apps:** instalar/remover por máquina ou grupo, com resultado por máquina; app já instalado conta como sucesso.
6. **Controle de acesso da TI:** cada técnico tem conta individual com verificação em duas etapas e permissões marcadas individualmente (ex.: suporte remoto; bloquear/desbloquear; instalar apps; administrar técnicos).
7. **Auditoria:** toda ação feita pelo gt_viewer fica registrada (quem, o quê, em qual máquina, quando, resultado).

## Fora de escopo (v1)
Celulares, máquinas Linux, macOS/iOS, integração com outros sistemas da empresa, conformidade, scripts avulsos, MAM, elevação de privilégios, IA, certificados, preparação automatizada, atualizações do Windows, BitLocker, localização.

## Casos de uso
- TI consulta quais máquinas existem e quem está usando cada uma.
- TI importa a planilha da locadora e confere se todas as máquinas do contrato estão no inventário.
- TI presta suporte remoto, com permissão do funcionário, sem deslocamento.
- TI bloqueia o acesso a uma máquina (perda, desligamento, não devolução) e desbloqueia depois.
- TI instala o mesmo aplicativo em várias máquinas.
- Administrador cria contas de técnicos e define o que cada um pode fazer.

## Restrições e fatos
- Custo de licença zero. Servidor da empresa com IP fixo e domínio próprio, acessível pela internet.
- O contrato de aluguel não restringe instalação de programas nem alteração de configurações; a locadora envia a lista de máquinas por e-mail em planilha Excel.
- Contas nas máquinas: parte domínio, parte local. Não há contas M365 para os técnicos.
- BitLocker não está ativo, então o bloqueio é por software: impede o uso normal, mas não resiste a quem formatar o disco.
- A empresa já formata as máquinas a cada troca de funcionário (3 técnicos dedicados); instalar o agente nesse momento é aceitável.
- Sem prazo definido.

## Perguntas abertas
1. Quais colunas tem a planilha da locadora? (Resolver no draft com uma planilha de exemplo.)
