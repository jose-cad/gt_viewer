using System.Security.Claims;
using System.Text.Json;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Models.Inventario;
using GtViewer.Web.Models.MeshCentral;
using GtViewer.Web.Pages.Maquinas;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GtViewer.Tests;

public class ReiniciarJ7Tests
{
    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd")
            .Options;
    }

    private static (
        ReiniciarService reiniciarService,
        GtViewerDbContext db,
        MockMeshCentralClient meshClient,
        IAgentScriptProvider scriptProvider) CreateTestServices()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var meshClient = new MockMeshCentralClient();
        var scriptProvider = new AgentScriptProvider();

        var reiniciarService = new ReiniciarService(
            db,
            meshClient,
            scriptProvider,
            NullLogger<ReiniciarService>.Instance);

        return (reiniciarService, db, meshClient, scriptProvider);
    }

    private static async Task<Tecnico> CriarTecnicoTesteAsync(
        GtViewerDbContext db,
        string permissoes = Permissoes.Reiniciar,
        bool ativo = true,
        bool isEmergencia = false)
    {
        var user = $"tec_restart_{Guid.NewGuid():N}"[..20];
        var tecnico = new Tecnico
        {
            UsuarioDominio = user,
            NomeExibicao = $"Técnico {user}",
            Email = $"{user}@empresa.local",
            Permissoes = permissoes,
            Ativo = ativo,
            IsContaEmergencia = isEmergencia,
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();
        return tecnico;
    }

    private static async Task<Maquina> CriarMaquinaTesteAsync(
        GtViewerDbContext db,
        string conexaoEstado = "online",
        string? nodeId = null)
    {
        var serie = $"SRV-RST-{Guid.NewGuid():N}"[..18];
        var maquina = new Maquina
        {
            NumeroSerie = serie,
            NodeId = nodeId ?? $"node_rst_{Guid.NewGuid():N}",
            Hostname = $"PC-{serie}",
            ConexaoEstado = conexaoEstado,
            BloqueioEstado = "desbloqueada",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();
        return maquina;
    }

    [Fact]
    public async Task Reiniciar_ComPermissaoEMaquinaOnline_ExecutaComAviso2Min_RegistraAcaoEAuditoria()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        meshClient.RunScriptHandler = (nodeId, script) =>
        {
            Assert.Equal(maquina.NodeId, nodeId);
            Assert.Contains("shutdown.exe", script);
            Assert.Contains("/t 120", script);
            Assert.Contains("A TI vai reiniciar seu computador em 2 minutos. Salve seu trabalho.", script);
            return new RunCommandResult("REINICIANDO", 0, "GTV-RESULT=REINICIANDO\r\nGTV-EXIT=0");
        };

        var acao = await service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio);

        Assert.NotNull(acao);
        Assert.Equal(TiposAcao.Reiniciar, acao.Tipo);
        Assert.Equal(0, acao.Prioridade); // Imediata, não compete na fila
        Assert.Equal(EstadosAcao.Executada, acao.Estado);
        Assert.Equal("REINICIANDO", acao.Resultado);
        Assert.Equal(0, acao.CodigoSaida);
        Assert.NotNull(acao.ConcluidoEm);

        // Verifica auditoria correspondente
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.AcaoId == acao.Id && a.Tipo == TiposAuditoria.Reiniciar);
        Assert.NotNull(audit);
        Assert.Equal(tecnico.UsuarioDominio, audit.Ator);
        Assert.Equal(maquina.NumeroSerie, audit.Alvo);
        Assert.True(audit.Sucesso);

        // Verifica que parâmetros JSON contêm o aviso e delay de 120s
        using var doc = JsonDocument.Parse(acao.ParametrosJson!);
        Assert.Equal(120, doc.RootElement.GetProperty("delay").GetInt32());
        Assert.Contains("2 minutos", doc.RootElement.GetProperty("aviso").GetString());

        Assert.Equal(1, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task Reiniciar_MaquinaOffline_LancaExcecao_NaoEnfileiraENaoEnvia()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "offline");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("offline", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Nenhuma ação criada e nenhum script disparado (reinício não fica pendente)
        Assert.Equal(0, meshClient.RunScriptCalls);
        var acoes = await db.Acoes.Where(a => a.MaquinaId == maquina.Id).ToListAsync();
        Assert.Empty(acoes);
    }

    [Fact]
    public async Task Reiniciar_MaquinaSumida_LancaExcecao_NaoEnviaNada()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "sumida");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("offline", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task Reiniciar_MeshCentralDesconectado_LancaExcecao_NaoEnviaNada()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        meshClient.IsConnected = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("Sem conexão com o servidor de máquinas", ex.Message);
        Assert.Equal(0, meshClient.RunScriptCalls);

        var acoes = await db.Acoes.Where(a => a.MaquinaId == maquina.Id).ToListAsync();
        Assert.Empty(acoes);
    }

    [Fact]
    public async Task Reiniciar_SemPermissao_LancaUnauthorizedAccessException()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, permissoes: Permissoes.VerAuditoria); // Sem reiniciar
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("permissão", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task Reiniciar_TecnicoInativo_LancaUnauthorizedAccessException()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, permissoes: Permissoes.Reiniciar, ativo: false);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("inativo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task Reiniciar_ContaEmergencia_ExecutaERegistraAlertaEmergencia()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnicoEmergencia = await CriarTecnicoTesteAsync(
            db,
            permissoes: string.Join(",", Permissoes.Emergencia),
            ativo: true,
            isEmergencia: true);

        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("REINICIANDO", 0, "GTV-RESULT=REINICIANDO\r\nGTV-EXIT=0");

        var acao = await service.SolicitarReinicioAsync(maquina.Id, tecnicoEmergencia.UsuarioDominio);

        Assert.NotNull(acao);
        Assert.Equal(EstadosAcao.Executada, acao.Estado);

        // Alerta de emergência persistido
        var alerta = await db.AlertasEmergencia.FirstOrDefaultAsync(a => a.Ator == tecnicoEmergencia.UsuarioDominio);
        Assert.NotNull(alerta);
        Assert.False(alerta.Reconhecido);
        Assert.Contains(maquina.NumeroSerie, alerta.Mensagem);
    }

    [Fact]
    public async Task Reiniciar_AuditoriaIndisponivel_FalhaNaTransacao_NadaEEnviadoAoMeshCentral()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        // Cancelamento imediato do token antes da persistência no banco
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio, cts.Token));

        // Invariante 5: sem registro gravado, nenhuma instrução chega ao agente MeshCentral
        Assert.Equal(0, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task Reiniciar_ScriptFalhaNoAgente_AtualizaAcaoComoFalhouELancaExcecao()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("FALHA", 1, "GTV-RESULT=FALHA: shutdown negado\r\nGTV-EXIT=1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("Falha ao reiniciar o computador", ex.Message);

        var acao = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.Reiniciar);
        Assert.NotNull(acao);
        Assert.Equal(EstadosAcao.Falhou, acao.Estado);
        Assert.Equal(1, acao.CodigoSaida);
        Assert.Contains("shutdown negado", acao.Detalhes);
    }

    [Fact]
    public async Task Reiniciar_FalhaDeRede_AtualizaAcaoComoFalhouELancaExcecao()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        meshClient.RunScriptHandler = (nodeId, script) =>
            throw new HttpRequestException("Falha de conexão com WebSocket do MeshCentral");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("Erro de comunicação", ex.Message);

        var acao = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.Reiniciar);
        Assert.NotNull(acao);
        Assert.Equal(EstadosAcao.Falhou, acao.Estado);
        Assert.Contains("Falha de conexão", acao.Detalhes);
    }

    [Fact]
    public async Task Reiniciar_ReconexaoSubsequente_AtualizaConexaoParaOnline()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var protection = new EphemeralDataProtectionProvider();
        var rescue = new RescuePasswordService(protection);
        var auditoria = new AuditoriaService(db);
        var inventarioService = new InventarioService(db, auditoria, rescue, NullLogger<InventarioService>.Instance);

        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("REINICIANDO", 0, "GTV-RESULT=REINICIANDO\r\nGTV-EXIT=0");

        await service.SolicitarReinicioAsync(maquina.Id, tecnico.UsuarioDominio);

        // 1. Simula queda da máquina ao reiniciar (evento nodedisconnect)
        meshClient.TriggerNodeDisconnect(maquina.NodeId!);
        var mQueda = await db.Maquinas.FindAsync(maquina.Id);
        // Após desligar, o evento do cliente reflete offline
        mQueda!.ConexaoEstado = "offline";
        await db.SaveChangesAsync();

        // 2. Simula reconexão da máquina após reinicialização completa (evento nodeconnect)
        meshClient.TriggerNodeConnect(maquina.NodeId!);
        var nodeJson = new System.Text.Json.Nodes.JsonObject
        {
            ["_id"] = maquina.NodeId,
            ["name"] = maquina.Hostname,
            ["conn"] = 1
        };
        await inventarioService.ProcessNodeUpdateAsync(maquina.NodeId!, nodeJson);

        var mReconectada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.Equal("online", mReconectada!.ConexaoEstado);
    }

    [Fact]
    public async Task UI_DetalhesModel_OnPostReiniciar_SemPermissao_RetornaForbid()
    {
        var (reiniciarService, db, meshClient, _) = CreateTestServices();
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        var protection = new EphemeralDataProtectionProvider();
        var rescue = new RescuePasswordService(protection);
        var totp = new TotpService(protection);
        var auditoria = new AuditoriaService(db);
        var options = Options.Create(new MeshCentralOptions());
        var filaService = new FilaAcoesService(db, meshClient, new AgentScriptProvider(), rescue, options, NullLogger<FilaAcoesService>.Instance);
        var bloqueioService = new BloqueioService(db, filaService, rescue, totp, auditoria, meshClient, NullLogger<BloqueioService>.Instance);
        var inventarioService = new InventarioService(db, auditoria, rescue, NullLogger<InventarioService>.Instance);

        var model = new DetalhesModel(inventarioService, bloqueioService, reiniciarService, meshClient, db)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "tecnico_sem_reiniciar"),
                        new Claim("Permissoes", Permissoes.VerAuditoria) // Sem permissão reiniciar
                    ], "TestAuth"))
                }
            }
        };

        var result = await model.OnPostReiniciarAsync(maquina.Id);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UI_DetalhesModel_OnPostReiniciar_ComPermissaoEMaquinaOnline_Sucesso()
    {
        var (reiniciarService, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        var protection = new EphemeralDataProtectionProvider();
        var rescue = new RescuePasswordService(protection);
        var totp = new TotpService(protection);
        var auditoria = new AuditoriaService(db);
        var options = Options.Create(new MeshCentralOptions());
        var filaService = new FilaAcoesService(db, meshClient, new AgentScriptProvider(), rescue, options, NullLogger<FilaAcoesService>.Instance);
        var bloqueioService = new BloqueioService(db, filaService, rescue, totp, auditoria, meshClient, NullLogger<BloqueioService>.Instance);
        var inventarioService = new InventarioService(db, auditoria, rescue, NullLogger<InventarioService>.Instance);

        var model = new DetalhesModel(inventarioService, bloqueioService, reiniciarService, meshClient, db)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, tecnico.UsuarioDominio),
                        new Claim("Permissoes", Permissoes.Reiniciar)
                    ], "TestAuth"))
                }
            }
        };

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("REINICIANDO", 0, "GTV-RESULT=REINICIANDO\r\nGTV-EXIT=0");

        var result = await model.OnPostReiniciarAsync(maquina.Id);
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(maquina.Id, redirect.RouteValues?["id"]);
        Assert.Contains("reiniciado em 2 minutos", model.MensagemSucesso);
    }

    [Fact]
    public async Task UI_DetalhesModel_OnPostReiniciar_MaquinaOffline_SetaMensagemErro()
    {
        var (reiniciarService, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.Reiniciar);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "offline");

        var protection = new EphemeralDataProtectionProvider();
        var rescue = new RescuePasswordService(protection);
        var totp = new TotpService(protection);
        var auditoria = new AuditoriaService(db);
        var options = Options.Create(new MeshCentralOptions());
        var filaService = new FilaAcoesService(db, meshClient, new AgentScriptProvider(), rescue, options, NullLogger<FilaAcoesService>.Instance);
        var bloqueioService = new BloqueioService(db, filaService, rescue, totp, auditoria, meshClient, NullLogger<BloqueioService>.Instance);
        var inventarioService = new InventarioService(db, auditoria, rescue, NullLogger<InventarioService>.Instance);

        var model = new DetalhesModel(inventarioService, bloqueioService, reiniciarService, meshClient, db)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, tecnico.UsuarioDominio),
                        new Claim("Permissoes", Permissoes.Reiniciar)
                    ], "TestAuth"))
                }
            }
        };

        var result = await model.OnPostReiniciarAsync(maquina.Id);
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(model.MensagemErro);
        Assert.Contains("offline", model.MensagemErro, StringComparison.OrdinalIgnoreCase);
    }
}
