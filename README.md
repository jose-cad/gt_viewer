# gt_viewer

Gerenciamento e suporte remoto de computadores Windows com custo de licença zero, integração com MeshCentral e auditoria estrita.

## Estrutura do Repositório

```
gt_viewer/
├── .spec/                         # Especificações e invariantes do sistema (SDD 2.0)
├── config/
│   ├── caddy/Caddyfile            # Configuração do proxy reverso Caddy (TLS/HTTPS, cabeçalhos, roteamento)
│   ├── meshcentral/config.json    # Configuração do MeshCentral (v1.3.1 fixada, tlsoffload, consentimento LGPD)
│   └── postgres/init.sql          # Inicialização dos bancos de dados PostgreSQL
├── scripts/
│   └── setup-firewall.sh          # Script de isolamento WAN via iptables DOCKER-USER (mantém rede interna/AD)
├── src/
│   └── GtViewer.Web/              # Aplicação ASP.NET Core (.NET 10 LTS, Razor Pages)
├── tests/
│   └── GtViewer.Tests/            # Testes automatizados (xUnit)
├── docker-compose.yml             # Orquestração (PostgreSQL, MeshCentral, Caddy, GtViewer.Web)
├── Dockerfile                     # Build multi-stage da aplicação Web em .NET 10
└── GtViewer.sln                   # Solução .NET 10
```

## Como Executar

### 1. Subir os serviços com Docker Compose

```bash
docker compose up -d
```

Serviços iniciados:
- **Caddy:** Porta 80 e 443 expostas no host, terminação TLS interna para desenvolvimento.
- **GtViewer.Web:** Aplicação web ASP.NET Core (.NET 10).
- **MeshCentral:** Versão 1.3.1 fixada, atrás do Caddy com `tlsoffload: true`.
- **PostgreSQL 17:** Bancos de dados `gtviewer` e `meshcentral`.

### 2. Acessar a aplicação

- **gt_viewer:** [http://localhost](http://localhost) ou [https://localhost](https://localhost)
- **MeshCentral (interno/admin):** [https://mesh.localhost](https://mesh.localhost)

### 3. Rodar os testes automatizados

```bash
dotnet test
```

### 4. Isolamento WAN (Produção / Staging)

Para aplicar as regras de isolamento que impedem acesso à WAN pelos contêineres, mantendo acesso à rede interna (AD LDAPS/636 e Caddy):

```bash
sudo ./scripts/setup-firewall.sh --apply
sudo ./scripts/setup-firewall.sh --check
```
