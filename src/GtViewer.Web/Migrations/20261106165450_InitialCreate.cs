using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GtViewer.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertasEmergencia",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DataHora = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Mensagem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Ator = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Reconhecido = table.Column<bool>(type: "boolean", nullable: false),
                    ReconhecidoPor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReconhecidoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertasEmergencia", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppsCatalogo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nome = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Versao = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FabricanteEsperado = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NomeArquivo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TamanhoBytes = table.Column<long>(type: "bigint", nullable: false),
                    ParametrosInstalacao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ComandoRemocao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RegraDeteccaoTipo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RegraDeteccaoChave = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RegraDeteccaoValor = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppsCatalogo", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Auditorias",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DataHora = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Ator = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Alvo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MaquinaId = table.Column<int>(type: "integer", nullable: true),
                    AcaoId = table.Column<int>(type: "integer", nullable: true),
                    Sucesso = table.Column<bool>(type: "boolean", nullable: false),
                    DetalhesJson = table.Column<string>(type: "text", nullable: true),
                    IpOrigem = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auditorias", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContratosImportacao",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NomeArquivo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TotalLinhas = table.Column<int>(type: "integer", nullable: false),
                    SeriesValidas = table.Column<int>(type: "integer", nullable: false),
                    SeriesRepetidas = table.Column<int>(type: "integer", nullable: false),
                    LinhasIgnoradas = table.Column<int>(type: "integer", nullable: false),
                    Confirmado = table.Column<bool>(type: "boolean", nullable: false),
                    Vigente = table.Column<bool>(type: "boolean", nullable: false),
                    Ator = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContratosImportacao", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Grupos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grupos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tecnicos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UsuarioDominio = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NomeExibicao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Ativo = table.Column<bool>(type: "boolean", nullable: false),
                    Permissoes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CodigoAtivacaoHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CodigoAtivacaoCriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CodigoAtivacaoTentativas = table.Column<int>(type: "integer", nullable: false),
                    TotpSecretCifrado = table.Column<string>(type: "text", nullable: true),
                    TotpConfigurado = table.Column<bool>(type: "boolean", nullable: false),
                    UltimoLoginEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TentativasFalhas = table.Column<int>(type: "integer", nullable: false),
                    TravadoAte = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tecnicos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContratosSeriais",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContratoImportacaoId = table.Column<int>(type: "integer", nullable: false),
                    NumeroSerie = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LinhaPlanilha = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContratosSeriais", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContratosSeriais_ContratosImportacao_ContratoImportacaoId",
                        column: x => x.ContratoImportacaoId,
                        principalTable: "ContratosImportacao",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Maquinas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NumeroSerie = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SerieManual = table.Column<bool>(type: "boolean", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Hostname = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Modelo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Fabricante = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SistemaOperacional = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Arquitetura = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AntivirusNome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AntivirusAtivo = table.Column<bool>(type: "boolean", nullable: true),
                    AntivirusAtualizado = table.Column<bool>(type: "boolean", nullable: true),
                    UsuariosLogadosJson = table.Column<string>(type: "text", nullable: true),
                    ConexaoEstado = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BloqueioEstado = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MensagemBloqueio = table.Column<string>(type: "text", nullable: true),
                    UltimoContatoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PrimeiroContatoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EmAprovacao = table.Column<bool>(type: "boolean", nullable: false),
                    CandidatoNodeId = table.Column<string>(type: "text", nullable: true),
                    CandidatoHostname = table.Column<string>(type: "text", nullable: true),
                    CandidatoIp = table.Column<string>(type: "text", nullable: true),
                    CandidatoPrimeiroContatoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CandidatoUltimoContatoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SenhaResgateAtualCifrada = table.Column<string>(type: "text", nullable: true),
                    SenhaResgateCandidataCifrada = table.Column<string>(type: "text", nullable: true),
                    SenhaResgateRevelada = table.Column<bool>(type: "boolean", nullable: false),
                    GrupoId = table.Column<int>(type: "integer", nullable: true),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maquinas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Maquinas_Grupos_GrupoId",
                        column: x => x.GrupoId,
                        principalTable: "Grupos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Tarefas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Tipo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AppCatalogoId = table.Column<int>(type: "integer", nullable: true),
                    GrupoId = table.Column<int>(type: "integer", nullable: true),
                    Ator = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcluidoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tarefas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tarefas_AppsCatalogo_AppCatalogoId",
                        column: x => x.AppCatalogoId,
                        principalTable: "AppsCatalogo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tarefas_Grupos_GrupoId",
                        column: x => x.GrupoId,
                        principalTable: "Grupos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HistoricoUsuarioMaquinas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaquinaId = table.Column<int>(type: "integer", nullable: false),
                    Usuario = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InicioEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FimEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricoUsuarioMaquinas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricoUsuarioMaquinas_Maquinas_MaquinaId",
                        column: x => x.MaquinaId,
                        principalTable: "Maquinas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TravasSessao",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaquinaId = table.Column<int>(type: "integer", nullable: false),
                    TecnicoUsername = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdentificadorSessao = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UsuarioLogadoNoMomento = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AdquiridaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UltimoHeartbeatEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiraEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravasSessao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravasSessao_Maquinas_MaquinaId",
                        column: x => x.MaquinaId,
                        principalTable: "Maquinas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Acoes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaquinaId = table.Column<int>(type: "integer", nullable: false),
                    TarefaId = table.Column<int>(type: "integer", nullable: true),
                    Tipo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Prioridade = table.Column<int>(type: "integer", nullable: false),
                    Estado = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Ator = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ParametrosJson = table.Column<string>(type: "text", nullable: true),
                    Resultado = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CodigoSaida = table.Column<int>(type: "integer", nullable: true),
                    Detalhes = table.Column<string>(type: "text", nullable: true),
                    Tentativas = table.Column<int>(type: "integer", nullable: false),
                    TokenAgenteHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TokenAgenteExpiraEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TokenAgenteUsado = table.Column<bool>(type: "boolean", nullable: false),
                    CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EnviadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcluidoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Acoes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Acoes_Maquinas_MaquinaId",
                        column: x => x.MaquinaId,
                        principalTable: "Maquinas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Acoes_Tarefas_TarefaId",
                        column: x => x.TarefaId,
                        principalTable: "Tarefas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Acoes_CriadoEm",
                table: "Acoes",
                column: "CriadoEm");

            migrationBuilder.CreateIndex(
                name: "IX_Acoes_Estado",
                table: "Acoes",
                column: "Estado");

            migrationBuilder.CreateIndex(
                name: "IX_Acoes_MaquinaId",
                table: "Acoes",
                column: "MaquinaId");

            migrationBuilder.CreateIndex(
                name: "IX_Acoes_Prioridade",
                table: "Acoes",
                column: "Prioridade");

            migrationBuilder.CreateIndex(
                name: "IX_Acoes_TarefaId",
                table: "Acoes",
                column: "TarefaId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertasEmergencia_DataHora",
                table: "AlertasEmergencia",
                column: "DataHora");

            migrationBuilder.CreateIndex(
                name: "IX_AlertasEmergencia_Reconhecido",
                table: "AlertasEmergencia",
                column: "Reconhecido");

            migrationBuilder.CreateIndex(
                name: "IX_AppsCatalogo_Nome",
                table: "AppsCatalogo",
                column: "Nome",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Auditorias_AcaoId",
                table: "Auditorias",
                column: "AcaoId");

            migrationBuilder.CreateIndex(
                name: "IX_Auditorias_Alvo",
                table: "Auditorias",
                column: "Alvo");

            migrationBuilder.CreateIndex(
                name: "IX_Auditorias_Ator",
                table: "Auditorias",
                column: "Ator");

            migrationBuilder.CreateIndex(
                name: "IX_Auditorias_DataHora",
                table: "Auditorias",
                column: "DataHora");

            migrationBuilder.CreateIndex(
                name: "IX_Auditorias_MaquinaId",
                table: "Auditorias",
                column: "MaquinaId");

            migrationBuilder.CreateIndex(
                name: "IX_Auditorias_Tipo",
                table: "Auditorias",
                column: "Tipo");

            migrationBuilder.CreateIndex(
                name: "IX_ContratosImportacao_Vigente",
                table: "ContratosImportacao",
                column: "Vigente");

            migrationBuilder.CreateIndex(
                name: "IX_ContratosSeriais_ContratoImportacaoId",
                table: "ContratosSeriais",
                column: "ContratoImportacaoId");

            migrationBuilder.CreateIndex(
                name: "IX_ContratosSeriais_NumeroSerie",
                table: "ContratosSeriais",
                column: "NumeroSerie");

            migrationBuilder.CreateIndex(
                name: "IX_Grupos_Nome",
                table: "Grupos",
                column: "Nome",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoUsuarioMaquinas_InicioEm",
                table: "HistoricoUsuarioMaquinas",
                column: "InicioEm");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoUsuarioMaquinas_MaquinaId",
                table: "HistoricoUsuarioMaquinas",
                column: "MaquinaId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricoUsuarioMaquinas_Usuario",
                table: "HistoricoUsuarioMaquinas",
                column: "Usuario");

            migrationBuilder.CreateIndex(
                name: "IX_Maquinas_BloqueioEstado",
                table: "Maquinas",
                column: "BloqueioEstado");

            migrationBuilder.CreateIndex(
                name: "IX_Maquinas_ConexaoEstado",
                table: "Maquinas",
                column: "ConexaoEstado");

            migrationBuilder.CreateIndex(
                name: "IX_Maquinas_GrupoId",
                table: "Maquinas",
                column: "GrupoId");

            migrationBuilder.CreateIndex(
                name: "IX_Maquinas_NodeId",
                table: "Maquinas",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Maquinas_NumeroSerie",
                table: "Maquinas",
                column: "NumeroSerie",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tarefas_AppCatalogoId",
                table: "Tarefas",
                column: "AppCatalogoId");

            migrationBuilder.CreateIndex(
                name: "IX_Tarefas_GrupoId",
                table: "Tarefas",
                column: "GrupoId");

            migrationBuilder.CreateIndex(
                name: "IX_Tecnicos_UsuarioDominio",
                table: "Tecnicos",
                column: "UsuarioDominio",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravasSessao_ExpiraEm",
                table: "TravasSessao",
                column: "ExpiraEm");

            migrationBuilder.CreateIndex(
                name: "IX_TravasSessao_MaquinaId",
                table: "TravasSessao",
                column: "MaquinaId",
                unique: true);

            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(Data.PostgresSecurityHelper.GenerateGrantSql());
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Acoes");

            migrationBuilder.DropTable(
                name: "AlertasEmergencia");

            migrationBuilder.DropTable(
                name: "Auditorias");

            migrationBuilder.DropTable(
                name: "ContratosSeriais");

            migrationBuilder.DropTable(
                name: "HistoricoUsuarioMaquinas");

            migrationBuilder.DropTable(
                name: "Tecnicos");

            migrationBuilder.DropTable(
                name: "TravasSessao");

            migrationBuilder.DropTable(
                name: "Tarefas");

            migrationBuilder.DropTable(
                name: "ContratosImportacao");

            migrationBuilder.DropTable(
                name: "Maquinas");

            migrationBuilder.DropTable(
                name: "AppsCatalogo");

            migrationBuilder.DropTable(
                name: "Grupos");
        }
    }
}
