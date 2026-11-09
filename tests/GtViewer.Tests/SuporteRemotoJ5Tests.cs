using System.Security.Claims;
using System.Text.Json;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Pages;
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

public class SuporteRemotoJ5Tests
{
    private const string FixedHexKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd")
            .Options;
    }

    private static (
        SuporteRemotoService suporteService,
        GtViewerDbContext db,
        MockMeshCentralClient meshClient,
        MeshLoginTokenService tokenService) CreateTestServices()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var meshClient = new MockMeshCentralClient();
        var tokenService = new MeshLoginTokenService(FixedHexKey, "https://mesh.local");

        var suporteService = new SuporteRemotoService(
            db,
            meshClient,
            tokenService,
            NullLogger<SuporteRemotoService>.Instance);

        return (suporteService, db, meshClient, tokenService);
    }

    private static async Task<Tecnico> CriarTecnicoTesteAsync(
        GtViewerDbContext db,
        string permissoes = Permissoes.SuporteRemoto,
        bool ativo = true,
        bool isEmergencia = false)
    {
        var user = $"tec_sup_{Guid.NewGuid():N}"[..20];
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
        List<string>? usuariosLogados = null)
    {
        var serie = $"SRV-SUP-{Guid.NewGuid():N}"[..18];
        var users = usuariosLogados ?? ["usuario_windows"];
        var maquina = new Maquina
        {
            NumeroSerie = serie,
            NodeId = $"node//rst_{Guid.NewGuid():N}",
            Hostname = $"PC-{serie}",
            ConexaoEstado = conexaoEstado,
            UsuariosLogadosJson = JsonSerializer.Serialize(users),
            BloqueioEstado = "desbloqueada",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();
        return maquina;
    }

    [Fact]
    public async Task IniciarSessao_ComPermissaoEMaquinaComUsuario_IniciaComSucesso_TravaAtiva_AcaoEAuditoria()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["mariasilva"]);

        var resultado = await service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio);

        Assert.NotNull(resultado);
        Assert.NotEmpty(resultado.SessionId);
        Assert.Equal("mariasilva", resultado.UsuarioAlvo);
        Assert.Equal($"gtv_mirror_{tecnico.UsuarioDominio}", resultado.MirrorUsername);
        Assert.Contains("viewmode=11", resultado.RemoteDesktopUrl);
        Assert.Contains("hide=31", resultado.RemoteDesktopUrl);

        // 1. Confere direitos concedidos no MeshCentral
        Assert.Equal(1, meshClient.EnsureMirrorUserCalls);
        Assert.Equal(1, meshClient.GrantRightsCalls);

        // 2. Confere trava atômica registrada no banco
        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.MaquinaId == maquina.Id);
        Assert.NotNull(trava);
        Assert.Equal(tecnico.UsuarioDominio, trava.TecnicoUsername);
        Assert.Equal("mariasilva", trava.UsuarioLogadoNoMomento);
        Assert.True(trava.ExpiraEm > DateTimeOffset.UtcNow);

        // 3. Confere Ação e Auditoria registradas na mesma transação
        var acao = await db.Acoes.FirstOrDefaultAsync(a => a.Id == resultado.AcaoId);
        Assert.NotNull(acao);
        Assert.Equal(TiposAcao.SuporteRemoto, acao.Tipo);
        Assert.Equal(EstadosAcao.Enviada, acao.Estado);

        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.AcaoId == acao.Id && a.Tipo == TiposAuditoria.AcessarTela);
        Assert.NotNull(audit);
        Assert.Equal(tecnico.UsuarioDominio, audit.Ator);
        Assert.True(audit.Sucesso);
    }

    [Fact]
    public async Task IniciarSessao_SemUsuarioLogado_LancaExcecaoEInvariante9()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: []); // Sem ninguém logado

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("usuário logado", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Invariante 9: sem usuário logado, nada é concedido no MeshCentral e nenhuma trava criada
        Assert.Equal(0, meshClient.GrantRightsCalls);
        var travas = await db.TravasSessao.Where(t => t.MaquinaId == maquina.Id).ToListAsync();
        Assert.Empty(travas);
    }

    [Fact]
    public async Task IniciarSessao_MaquinaOffline_LancaExcecao()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "offline");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("online", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, meshClient.GrantRightsCalls);
    }

    [Fact]
    public async Task IniciarSessao_MeshCentralDesconectado_LancaExcecao()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        meshClient.IsConnected = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("Sem conexão com o servidor de máquinas", ex.Message);
        Assert.Equal(0, meshClient.GrantRightsCalls);
    }

    [Fact]
    public async Task IniciarSessao_SemPermissao_LancaUnauthorizedAccessException()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, permissoes: Permissoes.VerAuditoria); // Sem suporte_remoto
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Equal(0, meshClient.GrantRightsCalls);
    }

    [Fact]
    public async Task IniciarSessao_SegundoTecnicoBarrado_ComAvisoDeQuemEstaConectado()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tec1 = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var tec2 = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        // Técnico 1 inicia atendimento
        await service.IniciarSessaoAsync(maquina.Id, tec1.UsuarioDominio);

        // Técnico 2 tenta iniciar simultaneamente na mesma máquina
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IniciarSessaoAsync(maquina.Id, tec2.UsuarioDominio));

        // Deve avisar exatamente quem está conectado
        Assert.Contains(tec1.UsuarioDominio, ex.Message);

        // Apenas as chamadas do Técnico 1 ocorreram
        Assert.Equal(1, meshClient.GrantRightsCalls);
    }

    [Fact]
    public async Task IniciarSessao_MesmoTecnicoRecarrega_RenovaSessaoEToken()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tec1 = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        var s1 = await service.IniciarSessaoAsync(maquina.Id, tec1.UsuarioDominio);
        var s2 = await service.IniciarSessaoAsync(maquina.Id, tec1.UsuarioDominio);

        Assert.NotNull(s2);
        Assert.Equal(s1.SessionId, s2.SessionId); // Mesma sessão mantida
    }

    [Fact]
    public async Task IniciarSessao_ContaEmergencia_GeraAlertaEmergencia()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecEmergencia = await CriarTecnicoTesteAsync(
            db,
            permissoes: string.Join(",", Permissoes.Emergencia),
            ativo: true,
            isEmergencia: true);

        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        var resultado = await service.IniciarSessaoAsync(maquina.Id, tecEmergencia.UsuarioDominio);
        Assert.NotNull(resultado);

        var alerta = await db.AlertasEmergencia.FirstOrDefaultAsync(a => a.Ator == tecEmergencia.UsuarioDominio);
        Assert.NotNull(alerta);
        Assert.False(alerta.Reconhecido);
        Assert.Contains(maquina.NumeroSerie, alerta.Mensagem);
    }

    [Fact]
    public async Task AuditoriaIndisponivel_FalhaNaTransacao_NadaEEnviadoAoMeshCentral()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio, cts.Token));

        // Invariante 5: sem registro gravado, nenhuma concessão feita
        Assert.Equal(0, meshClient.GrantRightsCalls);
    }

    [Fact]
    public async Task Heartbeat_RenovacaoACada30s_ExpiraEm90s()
    {
        var (service, db, _, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        var sessao = await service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio);

        var renovou = await service.RenovarHeartbeatAsync(sessao.SessionId);
        Assert.True(renovou);

        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.IdentificadorSessao == sessao.SessionId);
        Assert.NotNull(trava);
        Assert.True(trava.ExpiraEm > DateTimeOffset.UtcNow.AddSeconds(80));
    }

    [Fact]
    public async Task Heartbeat_Limite60Minutos_DerrubaSessaoERevogaDireitos()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        var sessao = await service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio);

        // Força AdquiridaEm para 61 minutos atrás
        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.IdentificadorSessao == sessao.SessionId);
        trava!.AdquiridaEm = DateTimeOffset.UtcNow.AddMinutes(-61);
        await db.SaveChangesAsync();

        var renovou = await service.RenovarHeartbeatAsync(sessao.SessionId);
        Assert.False(renovou); // Recusado

        // Trava liberada e sessão derrubada
        Assert.Equal(1, meshClient.DropSessionsCalls);
        Assert.Equal(1, meshClient.RevokeRightsCalls);
        var travaRemovida = await db.TravasSessao.FirstOrDefaultAsync(t => t.IdentificadorSessao == sessao.SessionId);
        Assert.Null(travaRemovida);
    }

    [Fact]
    public async Task Heartbeat_TecnicoDesativado_DerrubaSessaoERevogaDireitos()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        var sessao = await service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio);

        // Desativa o técnico no sistema
        tecnico.Ativo = false;
        await db.SaveChangesAsync();

        var renovou = await service.RenovarHeartbeatAsync(sessao.SessionId);
        Assert.False(renovou);

        Assert.Equal(1, meshClient.DropSessionsCalls);
        Assert.Equal(1, meshClient.RevokeRightsCalls);
        var travaRemovida = await db.TravasSessao.FirstOrDefaultAsync(t => t.IdentificadorSessao == sessao.SessionId);
        Assert.Null(travaRemovida);
    }

    [Fact]
    public async Task FecharAba_SemHeartbeat_WorkerReconciliaAposExpirar90s()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        var sessao = await service.IniciarSessaoAsync(maquina.Id, tecnico.UsuarioDominio);

        // Simula aba fechada: ExpiraEm no passado
        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.IdentificadorSessao == sessao.SessionId);
        trava!.ExpiraEm = DateTimeOffset.UtcNow.AddSeconds(-5);
        await db.SaveChangesAsync();

        // Worker roda reconciliação periódica
        await service.ReconciliarTravasOrfasAsync();

        Assert.Equal(1, meshClient.DropSessionsCalls);
        Assert.Equal(1, meshClient.RevokeRightsCalls);
        var travaRemovida = await db.TravasSessao.FirstOrDefaultAsync(t => t.IdentificadorSessao == sessao.SessionId);
        Assert.Null(travaRemovida);

        // Auditoria registrada
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.DetalhesJson!.Contains("trava_expirada") || a.DetalhesJson!.Contains("90s"));
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task SubidaGtViewer_RevogaTravasOrfasDeExecucaoAnterior()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        db.TravasSessao.RemoveRange(await db.TravasSessao.ToListAsync());
        await db.SaveChangesAsync();

        var tecnico = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        // Cria trava simulando reinicialização súbita do gt_viewer durante atendimento
        var travaOrfa = new TravaSessao
        {
            MaquinaId = maquina.Id,
            TecnicoUsername = tecnico.UsuarioDominio,
            IdentificadorSessao = "sess_orfa_reboot",
            UsuarioLogadoNoMomento = "joao",
            AdquiridaEm = DateTimeOffset.UtcNow.AddMinutes(-5),
            UltimoHeartbeatEm = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiraEm = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        db.TravasSessao.Add(travaOrfa);
        await db.SaveChangesAsync();

        // Simula worker StartAsync executando a reconciliação de subida
        var worker = new SuporteRemotoWorker(
            new CustomServiceProvider(db, meshClient, service),
            NullLogger<SuporteRemotoWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        // Confere que direitos e sessões foram revogados na subida
        Assert.Equal(1, meshClient.DropSessionsCalls);
        Assert.Equal(1, meshClient.RevokeRightsCalls);

        var travaRemovida = await db.TravasSessao.FirstOrDefaultAsync(t => t.IdentificadorSessao == "sess_orfa_reboot");
        Assert.Null(travaRemovida);

        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.Tipo == TiposAuditoria.RevogarDireitoOrfaoTela);
        Assert.NotNull(audit);
        Assert.Equal("sistema", audit.Ator);
    }

    [Fact]
    public async Task DesativarTecnico_EncerraSessaoNaHora()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tec = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["joao"]);

        await service.IniciarSessaoAsync(maquina.Id, tec.UsuarioDominio);

        var activation = new ActivationCodeService();
        var auditoria = new AuditoriaService(db);
        var tecnicoService = new TecnicoService(db, activation, auditoria, service);

        // Desativação do técnico
        await tecnicoService.DesativarTecnicoAsync(tec.Id, "admin");

        // Sessão derrubada ativamente e direitos revogados
        Assert.Equal(1, meshClient.DropSessionsCalls);
        Assert.Equal(1, meshClient.RevokeRightsCalls);

        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.MaquinaId == maquina.Id);
        Assert.Null(trava);
    }

    [Fact]
    public async Task LogoffOuBloqueioTela_EncerraSessaoNaHora()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tec = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["mariasilva"]);

        await service.IniciarSessaoAsync(maquina.Id, tec.UsuarioDominio);

        var protection = new EphemeralDataProtectionProvider();
        var rescue = new RescuePasswordService(protection);
        var auditoria = new AuditoriaService(db);
        var inventarioService = new InventarioService(db, auditoria, rescue, NullLogger<InventarioService>.Instance, null, service);

        // Agente reporta telemetria atualizada sem "mariasilva" (logoff ou bloqueio de tela)
        var rawNode = new System.Text.Json.Nodes.JsonObject
        {
            ["_id"] = maquina.NodeId,
            ["name"] = maquina.Hostname,
            ["conn"] = 1,
            ["users"] = new System.Text.Json.Nodes.JsonArray() // Nenhum usuário interativo
        };

        await inventarioService.ProcessNodeUpdateAsync(maquina.NodeId!, rawNode);

        // A sessão é encerrada na hora
        Assert.Equal(1, meshClient.DropSessionsCalls);
        Assert.Equal(1, meshClient.RevokeRightsCalls);

        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.MaquinaId == maquina.Id);
        Assert.Null(trava);
    }

    [Fact]
    public async Task ConsentimentoRecusado_OuSemResposta60s_MsgId34_RegistraRecusaELiberaTrava()
    {
        var (service, db, meshClient, _) = CreateTestServices();
        var tec = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["mariasilva"]);

        var sessao = await service.IniciarSessaoAsync(maquina.Id, tec.UsuarioDominio);

        // Simula evento msgid: 34 (Failed to start remote desktop after local user rejected / timeout 60s)
        await service.RegistrarRecusaAsync(sessao.SessionId, "Acesso recusado pelo usuário em 60s");

        Assert.Equal(1, meshClient.DropSessionsCalls);
        Assert.Equal(1, meshClient.RevokeRightsCalls);

        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.MaquinaId == maquina.Id);
        Assert.Null(trava);

        // Auditoria com Sucesso = false
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.AcaoId == sessao.AcaoId && !a.Sucesso);
        Assert.NotNull(audit);
        Assert.Contains("recusa", audit.DetalhesJson);
    }

    [Fact]
    public async Task UI_RemoteSupportModel_EncerraSessao_ComPost()
    {
        var (service, db, meshClient, tokenService) = CreateTestServices();
        var tec = await CriarTecnicoTesteAsync(db, Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, conexaoEstado: "online", usuariosLogados: ["mariasilva"]);

        var sessao = await service.IniciarSessaoAsync(maquina.Id, tec.UsuarioDominio);

        var model = new RemoteSupportModel(service, meshClient, db, tokenService)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, tec.UsuarioDominio),
                        new Claim("Permissoes", Permissoes.SuporteRemoto)
                    ], "TestAuth"))
                }
            }
        };

        var result = await model.OnPostEncerrarAsync(sessao.SessionId);
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Maquinas/Index", redirect.PageName);

        var trava = await db.TravasSessao.FirstOrDefaultAsync(t => t.MaquinaId == maquina.Id);
        Assert.Null(trava);
    }

    private class CustomServiceProvider : IServiceProvider
    {
        private readonly GtViewerDbContext _db;
        private readonly IMeshCentralClient _mesh;
        private readonly ISuporteRemotoService _suporte;

        public CustomServiceProvider(GtViewerDbContext db, IMeshCentralClient mesh, ISuporteRemotoService suporte)
        {
            _db = db;
            _mesh = mesh;
            _suporte = suporte;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory))
            {
                return new CustomScopeFactory(this);
            }
            if (serviceType == typeof(GtViewerDbContext)) return _db;
            if (serviceType == typeof(IMeshCentralClient)) return _mesh;
            if (serviceType == typeof(ISuporteRemotoService)) return _suporte;
            return null;
        }
    }

    private class CustomScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        private readonly IServiceProvider _sp;
        public CustomScopeFactory(IServiceProvider sp) => _sp = sp;
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new CustomScope(_sp);
    }

    private class CustomScope : Microsoft.Extensions.DependencyInjection.IServiceScope
    {
        public CustomScope(IServiceProvider sp) => ServiceProvider = sp;
        public IServiceProvider ServiceProvider { get; }
        public void Dispose() { }
    }
}
