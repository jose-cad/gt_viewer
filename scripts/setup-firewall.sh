#!/usr/bin/env bash
# ==============================================================================
# setup-firewall.sh - Isolamento WAN para contêineres gt_viewer
# ==============================================================================
# Requisito (spec.md:232 / review.md:203 M1):
# Contêineres sem saída para a internet (WAN), mantendo acesso à rede interna
# (AD LDAPS/636, Caddy interno e tráfego intra-Docker).
#
# Utiliza a cadeia DOCKER-USER do iptables no host para bloquear tráfego de saída
# para a WAN originado da subnet Docker (172.28.0.0/16), preservando RFC 1918 (LAN).
# ==============================================================================

set -euo pipefail

SUBNET="${GTV_SUBNET:-172.28.0.0/16}"
COMMENT_TAG="gtv-wan-isolation"

usage() {
    cat <<EOF
Uso: $0 [OPÇÃO]

Opções:
  --apply      Aplica as regras iptables na cadeia DOCKER-USER
  --check      Verifica se as regras estão ativas
  --flush      Remove as regras de isolamento adicionadas por este script
  --help       Exibe esta ajuda

Variáveis de ambiente opcionais:
  GTV_SUBNET   Subnet dos contêineres (padrão: 172.28.0.0/16)
EOF
}

check_root() {
    if [[ $EUID -ne 0 ]]; then
        echo "Aviso: Este script requer privilégios de root (sudo) para alterar regras do iptables." >&2
        return 1
    fi
    return 0
}

flush_rules() {
    echo "Removendo regras com tag '${COMMENT_TAG}'..."
    # Localiza e remove linhas que contêm a tag do comentário
    iptables -S DOCKER-USER 2>/dev/null | grep "${COMMENT_TAG}" | while read -r line; do
        rule_args="${line#-A }"
        iptables -D ${rule_args} 2>/dev/null || true
    done
    echo "Regras removidas com sucesso."
}

apply_rules() {
    echo "Aplicando regras de isolamento WAN para subnet ${SUBNET}..."
    flush_rules

    # 1. Manter conexões já estabelecidas / relacionadas
    iptables -I DOCKER-USER 1 -m conntrack --ctstate RELATED,ESTABLISHED -m comment --comment "${COMMENT_TAG}" -j ACCEPT

    # 2. Permitir tráfego interno entre contêineres da mesma subnet
    iptables -I DOCKER-USER 2 -s "${SUBNET}" -d "${SUBNET}" -m comment --comment "${COMMENT_TAG}" -j ACCEPT

    # 3. Permitir tráfego para redes internas RFC 1918 (AD LDAPS/636, roteadores, DNS corporativo)
    iptables -I DOCKER-USER 3 -s "${SUBNET}" -d 10.0.0.0/8 -m comment --comment "${COMMENT_TAG}" -j ACCEPT
    iptables -I DOCKER-USER 4 -s "${SUBNET}" -d 172.16.0.0/12 -m comment --comment "${COMMENT_TAG}" -j ACCEPT
    iptables -I DOCKER-USER 5 -s "${SUBNET}" -d 192.168.0.0/16 -m comment --comment "${COMMENT_TAG}" -j ACCEPT

    # 4. Bloquear qualquer outra saída WAN para os contêineres do gt_viewer
    iptables -I DOCKER-USER 6 -s "${SUBNET}" ! -o docker0 -m comment --comment "${COMMENT_TAG}" -j REJECT --reject-with icmp-host-prohibited

    echo "Regras aplicadas com sucesso:"
    iptables -L DOCKER-USER -v -n --line-numbers | grep -E "${COMMENT_TAG}|Chain"
}

check_rules() {
    echo "Estado atual da cadeia DOCKER-USER para '${COMMENT_TAG}':"
    if iptables -S DOCKER-USER 2>/dev/null | grep -q "${COMMENT_TAG}"; then
        iptables -L DOCKER-USER -v -n --line-numbers | grep -E "${COMMENT_TAG}|Chain"
        echo -e "\n[OK] Regras de isolamento WAN ativas."
        return 0
    else
        echo -e "\n[INFO] Nenhuma regra com tag '${COMMENT_TAG}' encontrada."
        return 1
    fi
}

case "${1:---help}" in
    --apply)
        check_root
        apply_rules
        ;;
    --check)
        check_rules
        ;;
    --flush)
        check_root
        flush_rules
        ;;
    --help|-h)
        usage
        ;;
    *)
        echo "Opção desconhecida: $1" >&2
        usage
        exit 1
        ;;
esac
