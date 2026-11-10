using System.Net;
using System.Reflection;
using System.Text.Json;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GtViewer.Tests;

public class AuditoriaRetencaoJ10Tests : IClassFixture<WebApplicationFactory<GtViewer.Web.Program>>
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtv_app;Password=gtv_app_secret_pass_2026!";

    private const string RetentionConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtv_retention;Password=gtv_retention_secret_pass_2026!";

    private readonly WebApplicationFactory<GtViewer.Web.Program> _factory;

    public AuditoriaRetencaoJ10Tests(WebApplicationFactory<GtViewer.Web.Program> factory)
    {
        _factory = factory;
    }

    private static DbContextOptions<GtViewerDbContext> CreateAdminOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private static DbContextOptions<GtViewerDbContext> CreateAppOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AppConnectionString)
            .Options;
    }

    private static DbContextOptions<GtViewerDbContext> CreateRetentionOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(RetentionConnectionString)
            .Options;
    }

    private static (AuditoriaService service, GtViewerDbContext db) CreateAuditoriaService()
    {
        var db = new GtViewerDbContext(CreateAdminOptions());
        var service = new AuditoriaService(db);
        return (service, db);
    }

    private static RetentionService CreateRetentionService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RetentionConnection"] = RetentionConnectionString,
                ["ConnectionStrings:DefaultConnection"] = AppConnectionString
            })
            .Build();

        return new RetentionService(configuration, NullLogger<RetentionService>.Instance);
    }

    private (HttpClient Client, IDisposable Scope) CreateClientWithAuth(params string[] permissoes)
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, options => { });
            });
        });

        var client = customFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        client.DefaultRequestHeaders.Add("X-Test-User", "tecnico.auditor");
        client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(",", permissoes));

        return (client, customFactory);
    }

    [Fact]
    public async Task ListaFechada_TodosOs30TiposDeAuditoria_ConferemEmAuditoria_AcaoApenasEmAcoesDeMaquina_SemSegredos()
    {
        var (service, db) = CreateAuditoriaService();

        // 1. Obtém todos os tipos de TiposAuditoria por reflexão
        var todosTipos = typeof(TiposAuditoria)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        // Deve conter os 30 tipos exigidos pela Convenção
        Assert.True(todosTipos.Count >= 30, $"Esperava ao menos 30 tipos auditados na lista fechada, encontrados: {todosTipos.Count}");

        // Lista de ações de máquina (que obrigatoriamente vinculam AcaoId)
        var tiposAcaoDeMaquina = new HashSet<string>
        {
            TiposAuditoria.Bloquear,
            TiposAuditoria.Desbloquear,
            TiposAuditoria.GarantirContaResgate,
            TiposAuditoria.TrocarSenhaResgate,
            TiposAuditoria.RedefinirContaResgate,
            TiposAuditoria.Instalar,
            TiposAuditoria.Remover,
            TiposAuditoria.Reiniciar,
            TiposAuditoria.AcessarTela,
            TiposAuditoria.VerificarReaplicarBloqueio,
            TiposAuditoria.CancelarPendencia
        };

        // Cria máquina de teste para vincular
        var maquina = new Maquina
        {
            NumeroSerie = "SN-AUDIT-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Hostname = "NB-AUDIT-LISTA",
            ConexaoEstado = "online",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        // 2. Percorre cada tipo e confere o registro e as regras
        foreach (var tipo in todosTipos)
        {
            int? acaoId = null;
            int? maquinaId = null;

            if (tiposAcaoDeMaquina.Contains(tipo))
            {
                // Ação de máquina: deve ter registro em Acao
                var acao = new Acao
                {
                    MaquinaId = maquina.Id,
                    Tipo = tipo,
                    Estado = EstadosAcao.Executada,
                    Ator = "tecnico.teste",
                    CriadoEm = DateTimeOffset.UtcNow,
                    Prioridade = 1
                };
                db.Acoes.Add(acao);
                await db.SaveChangesAsync();

                acaoId = acao.Id;
                maquinaId = maquina.Id;
            }

            var detalhes = JsonSerializer.Serialize(new
            {
                evento = tipo,
                timestamp = DateTimeOffset.UtcNow,
                sucesso = true,
                info = "Operação executada conforme política de segurança."
            });

            var audit = await service.RegistrarAsync(
                ator: "tecnico.auditor",
                tipo: tipo,
                alvo: maquina.NumeroSerie,
                sucesso: true,
                detalhesJson: detalhes,
                ipOrigem: "192.168.1.50",
                maquinaId: maquinaId,
                acaoId: acaoId
            );

            Assert.True(audit.Id > 0);
            Assert.Equal(tipo, audit.Tipo);

            // Invariante da Tarefa 16: com Acao apenas nas ações de máquina
            if (tiposAcaoDeMaquina.Contains(tipo))
            {
                Assert.NotNull(audit.AcaoId);
                Assert.NotNull(audit.MaquinaId);
            }
            else
            {
                Assert.Null(audit.AcaoId);
            }

            // Ausência de segredos: detalhesJson não deve conter senhas ou segredos
            Assert.False(audit.DetalhesJson?.Contains("password", StringComparison.OrdinalIgnoreCase) ?? false);
            Assert.False(audit.DetalhesJson?.Contains("secretKey", StringComparison.OrdinalIgnoreCase) ?? false);
            Assert.False(audit.DetalhesJson?.Contains("totpSecret", StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }

    [Fact]
    public async Task ConsultaAuditoria_Filtros_Ator_Maquina_Tipo_Periodo_Sucesso_EstadoAcao()
    {
        var (service, db) = CreateAuditoriaService();

        var snUnico = "SN-FILTRO-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var maquina = new Maquina
        {
            NumeroSerie = snUnico,
            Hostname = "NB-FILTRO-TESTE",
            ConexaoEstado = "online",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        var acaoExec = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = TiposAcao.Bloquear,
            Estado = EstadosAcao.Executada,
            Ator = "maria.silva",
            CriadoEm = DateTimeOffset.UtcNow,
            Prioridade = 1
        };
        db.Acoes.Add(acaoExec);
        await db.SaveChangesAsync();

        var atorUnico = "ator.teste." + Guid.NewGuid().ToString("N")[..4];

        // Cria 3 registros variados
        await service.RegistrarAsync(
            ator: atorUnico,
            tipo: TiposAuditoria.LoginOk,
            alvo: "gt_viewer",
            sucesso: true,
            detalhesJson: "{\"ip\":\"10.0.0.1\"}"
        );

        await service.RegistrarAsync(
            ator: atorUnico,
            tipo: TiposAuditoria.Bloquear,
            alvo: maquina.NumeroSerie,
            sucesso: true,
            detalhesJson: "{\"motivo\":\"Filtro teste\"}",
            maquinaId: maquina.Id,
            acaoId: acaoExec.Id
        );

        await service.RegistrarAsync(
            ator: "sistema",
            tipo: TiposAuditoria.LoginFalho,
            alvo: "gt_viewer",
            sucesso: false,
            detalhesJson: "{\"motivo\":\"Senha incorreta\"}"
        );

        // 1. Filtro por Ator
        var resAtor = await service.ConsultarAsync(new AuditoriaFiltro { Ator = atorUnico });
        Assert.Equal(2, resAtor.Itens.Count);
        Assert.All(resAtor.Itens, item => Assert.Equal(atorUnico, item.Ator));

        // 2. Filtro por Máquina
        var resMaq = await service.ConsultarAsync(new AuditoriaFiltro { Maquina = snUnico });
        Assert.True(resMaq.Itens.Count >= 1);
        Assert.Contains(resMaq.Itens, i => i.Alvo == snUnico || i.MaquinaNumeroSerie == snUnico);

        // 3. Filtro por Tipo de Ação
        var resTipo = await service.ConsultarAsync(new AuditoriaFiltro { Ator = atorUnico, Tipo = TiposAuditoria.Bloquear });
        Assert.Single(resTipo.Itens);
        Assert.Equal(TiposAuditoria.Bloquear, resTipo.Itens[0].Tipo);

        // 4. Filtro por Sucesso (false)
        var resFalha = await service.ConsultarAsync(new AuditoriaFiltro { Tipo = TiposAuditoria.LoginFalho, Sucesso = false });
        Assert.True(resFalha.Itens.Count >= 1);
        Assert.All(resFalha.Itens, i => Assert.False(i.Sucesso));

        // 5. Filtro por Estado da Ação associada (Executada)
        var resEstadoAcao = await service.ConsultarAsync(new AuditoriaFiltro { Ator = atorUnico, EstadoAcao = EstadosAcao.Executada });
        Assert.Single(resEstadoAcao.Itens);
        Assert.Equal(EstadosAcao.Executada, resEstadoAcao.Itens[0].AcaoEstado);

        // 6. Exportação para Excel
        var excelBytes = await service.ExportarExcelAsync(new AuditoriaFiltro { Ator = atorUnico });
        Assert.NotNull(excelBytes);
        Assert.True(excelBytes.Length > 1000);
    }

    [Fact]
    public async Task WebUi_Permissoes_VerAuditoria_Enforced()
    {
        var clientFactory = _factory.WithWebHostBuilder(_ => { });

        // 1. Acesso anônimo a /Auditoria/Index -> 302 Redireciona para /Login
        var anonClient = clientFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonResponse = await anonClient.GetAsync("/Auditoria/Index");
        Assert.Equal(HttpStatusCode.Redirect, anonResponse.StatusCode);
        Assert.Contains("/login", anonResponse.Headers.Location?.ToString().ToLower());

        // 2. Acesso autenticado SEM ver_auditoria -> 403 Forbidden ou Redirect para erro 403
        var (semPermissaoClient, _) = CreateClientWithAuth(Permissoes.SuporteRemoto);
        var semPermissaoResponse = await semPermissaoClient.GetAsync("/Auditoria/Index");
        Assert.True(
            semPermissaoResponse.StatusCode == HttpStatusCode.Forbidden ||
            (semPermissaoResponse.StatusCode == HttpStatusCode.Redirect &&
             semPermissaoResponse.Headers.Location?.ToString().Contains("403") == true),
            $"Esperava 403 ou redirecionamento para página de acesso negado 403, obtido: {semPermissaoResponse.StatusCode}");

        // 3. Acesso autenticado COM ver_auditoria -> 200 OK
        var (comPermissaoClient, _) = CreateClientWithAuth(Permissoes.VerAuditoria);
        var comPermissaoResponse = await comPermissaoClient.GetAsync("/Auditoria/Index");
        Assert.Equal(HttpStatusCode.OK, comPermissaoResponse.StatusCode);
        var html = await comPermissaoResponse.Content.ReadAsStringAsync();
        Assert.Contains("Auditoria de Ações e Segurança", html);
    }

    [Fact]
    public async Task RotinaRetencao_RemoveRegistrosComMaisDe2Anos_EPreservaMaisRecentes()
    {
        using var adminDb = new GtViewerDbContext(CreateAdminOptions());
        var retentionService = CreateRetentionService();

        var agora = DateTimeOffset.UtcNow;
        var corte2Anos = agora.AddYears(-2);

        // 1. Cria máquina para vincular ações e histórico
        var maquina = new Maquina
        {
            NumeroSerie = "SN-RETENCAO-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Hostname = "NB-RETENCAO",
            ConexaoEstado = "online",
            CriadoEm = agora.AddYears(-3),
            UltimoContatoEm = agora
        };
        adminDb.Maquinas.Add(maquina);
        await adminDb.SaveChangesAsync();

        // 2. Inserir em Auditorias: 2 expirados (> 2 anos) e 2 recentes (< 2 anos)
        var auditExpirada1 = new Auditoria
        {
            DataHora = corte2Anos.AddDays(-15),
            Ator = "retencao.teste",
            Tipo = TiposAuditoria.LoginOk,
            Alvo = "gt_viewer",
            Sucesso = true
        };
        var auditExpirada2 = new Auditoria
        {
            DataHora = corte2Anos.AddMonths(-3),
            Ator = "retencao.teste",
            Tipo = TiposAuditoria.LoginFalho,
            Alvo = "gt_viewer",
            Sucesso = false
        };
        var auditRecente1 = new Auditoria
        {
            DataHora = corte2Anos.AddDays(15),
            Ator = "retencao.teste",
            Tipo = TiposAuditoria.LoginOk,
            Alvo = "gt_viewer",
            Sucesso = true
        };
        var auditRecente2 = new Auditoria
        {
            DataHora = agora,
            Ator = "retencao.teste",
            Tipo = TiposAuditoria.LoginOk,
            Alvo = "gt_viewer",
            Sucesso = true
        };
        adminDb.Auditorias.AddRange(auditExpirada1, auditExpirada2, auditRecente1, auditRecente2);

        // 3. Inserir em HistoricoUsuarioMaquinas: 1 expirado e 1 recente
        var histExpirado = new HistoricoUsuarioMaquina
        {
            MaquinaId = maquina.Id,
            Usuario = "usuario.antigo",
            InicioEm = corte2Anos.AddMonths(-2),
            FimEm = corte2Anos.AddMonths(-1)
        };
        var histRecente = new HistoricoUsuarioMaquina
        {
            MaquinaId = maquina.Id,
            Usuario = "usuario.recente",
            InicioEm = agora.AddDays(-10),
            FimEm = null
        };
        adminDb.HistoricoUsuarioMaquinas.AddRange(histExpirado, histRecente);

        // 4. Inserir em Acoes: 1 expirada e 1 recente
        var acaoExpirada = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = TiposAcao.Bloquear,
            Estado = EstadosAcao.Executada,
            Ator = "retencao.teste",
            CriadoEm = corte2Anos.AddDays(-5),
            Prioridade = 1
        };
        var acaoRecente = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = TiposAcao.Desbloquear,
            Estado = EstadosAcao.Executada,
            Ator = "retencao.teste",
            CriadoEm = agora.AddDays(-5),
            Prioridade = 1
        };
        adminDb.Acoes.AddRange(acaoExpirada, acaoRecente);

        await adminDb.SaveChangesAsync();

        var idAuditExp1 = auditExpirada1.Id;
        var idAuditExp2 = auditExpirada2.Id;
        var idAuditRec1 = auditRecente1.Id;
        var idAuditRec2 = auditRecente2.Id;
        var idHistExp = histExpirado.Id;
        var idHistRec = histRecente.Id;
        var idAcaoExp = acaoExpirada.Id;
        var idAcaoRec = acaoRecente.Id;

        // 5. Executa expurgo usando o usuário gtv_retention
        var resultado = await retentionService.ExecutarExpurgoAsync(referencia: agora);

        Assert.True(resultado.AuditoriasExcluidas >= 2);
        Assert.True(resultado.HistoricosExcluidos >= 1);
        Assert.True(resultado.AcoesExcluidas >= 1);

        // 6. Confere no banco que os registros expirados foram excluídos
        using var conferirDb = new GtViewerDbContext(CreateAdminOptions());
        Assert.Null(await conferirDb.Auditorias.FindAsync(idAuditExp1));
        Assert.Null(await conferirDb.Auditorias.FindAsync(idAuditExp2));
        Assert.Null(await conferirDb.HistoricoUsuarioMaquinas.FindAsync(idHistExp));
        Assert.Null(await conferirDb.Acoes.FindAsync(idAcaoExp));

        // 7. Confere no banco que os registros recentes foram rigorosamente preservados
        Assert.NotNull(await conferirDb.Auditorias.FindAsync(idAuditRec1));
        Assert.NotNull(await conferirDb.Auditorias.FindAsync(idAuditRec2));
        Assert.NotNull(await conferirDb.HistoricoUsuarioMaquinas.FindAsync(idHistRec));
        Assert.NotNull(await conferirDb.Acoes.FindAsync(idAcaoRec));
    }

    [Fact]
    public async Task ReconhecerAlertaEmergencia_AuditaComoReconhecerAlerta_SemAcao()
    {
        var (service, db) = CreateAuditoriaService();

        var alerta = new AlertaEmergencia
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = "emergencia",
            Mensagem = "Uso de conta de emergência na máquina de teste",
            Reconhecido = false
        };
        db.AlertasEmergencia.Add(alerta);
        await db.SaveChangesAsync();

        // Administrador reconhece o alerta
        await service.ReconhecerAlertaEmergenciaAsync(alerta.Id, "admin.auditor");

        // Confere que o alerta foi atualizado
        var alertaAtualizado = await db.AlertasEmergencia.FindAsync(alerta.Id);
        Assert.NotNull(alertaAtualizado);
        Assert.True(alertaAtualizado.Reconhecido);
        Assert.Equal("admin.auditor", alertaAtualizado.ReconhecidoPor);
        Assert.NotNull(alertaAtualizado.ReconhecidoEm);

        // Confere registro na tabela Auditoria
        var audit = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.ReconhecerAlerta && a.Ator == "admin.auditor")
            .OrderByDescending(a => a.DataHora)
            .FirstOrDefaultAsync();

        Assert.NotNull(audit);
        Assert.Null(audit.AcaoId); // Ação administrativa não possui AcaoId
        Assert.True(audit.Sucesso);
        Assert.Contains(alerta.Id.ToString(), audit.DetalhesJson);
    }

    [Fact]
    public async Task AlterarGrupo_AuditaComoAlterarGrupo_SemAcao()
    {
        var (service, db) = CreateAuditoriaService();

        var grupo = new Grupo
        {
            Nome = "Grupo Antigo " + Guid.NewGuid().ToString("N")[..4],
            Descricao = "Descrição inicial",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Grupos.Add(grupo);
        await db.SaveChangesAsync();

        var novoNome = "Grupo Novo " + Guid.NewGuid().ToString("N")[..4];
        await service.AlterarGrupoAsync(grupo.Id, novoNome, "Descrição atualizada", "admin.ti");

        var grupoAtualizado = await db.Grupos.FindAsync(grupo.Id);
        Assert.NotNull(grupoAtualizado);
        Assert.Equal(novoNome, grupoAtualizado.Nome);

        var audit = await db.Auditorias
            .Where(a => a.Tipo == TiposAuditoria.AlterarGrupo && a.Ator == "admin.ti")
            .OrderByDescending(a => a.DataHora)
            .FirstOrDefaultAsync();

        Assert.NotNull(audit);
        Assert.Null(audit.AcaoId); // Ação administrativa não possui AcaoId
        Assert.True(audit.Sucesso);
        Assert.Contains(novoNome, audit.DetalhesJson);
    }
}
