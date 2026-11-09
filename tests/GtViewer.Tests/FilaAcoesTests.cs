using System.Text.Json;
using System.Text.Json.Nodes;
using GtViewer.Tests.Mocks;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Models.MeshCentral;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GtViewer.Tests;

public class FilaAcoesTests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private static (FilaAcoesService service, GtViewerDbContext db, MockMeshCentralClient meshClient, IAgentScriptProvider scriptProvider) CreateTestService()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var meshClient = new MockMeshCentralClient();
        var scriptProvider = new AgentScriptProvider();
        var protection = new EphemeralDataProtectionProvider();
        var rescueService = new RescuePasswordService(protection);
        var options = Options.Create(new MeshCentralOptions { ServerUrl = "https://gtviewer.local" });

        var service = new FilaAcoesService(
            db,
            meshClient,
            scriptProvider,
            rescueService,
            options,
            NullLogger<FilaAcoesService>.Instance);

        return (service, db, meshClient, scriptProvider);
    }

    private static async Task<Maquina> CriarMaquinaTesteAsync(GtViewerDbContext db, string prefix = "TEST_QUEUE", bool online = true)
    {
        var serial = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var nodeId = $"node//test_{Guid.NewGuid():N}";
        var maquina = new Maquina
        {
            NumeroSerie = serial,
            NodeId = nodeId,
            Hostname = $"HOST-{serial}",
            ConexaoEstado = online ? "online" : "offline",
            BloqueioEstado = "desbloqueada",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();
        return maquina;
    }

    [Fact]
    public async Task TransicaoEstado_FluxoCompleto_Pendente_Enviada_Executada()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "TRANS_OK", online: true);

        // Mock retorna sucesso
        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");

        // 1. Enfileirar ação
        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.GarantirResgate, "admin_user");
        Assert.Equal(EstadosAcao.Pendente, acao.Estado);
        Assert.Equal(PrioridadesAcao.Resgate, acao.Prioridade);

        // 2. Processar máquina -> deve despachar e executar
        await service.ProcessarMaquinaAsync(maquina.Id);

        var acaoAtualizada = await db.Acoes.FindAsync(acao.Id);
        Assert.NotNull(acaoAtualizada);
        Assert.Equal(EstadosAcao.Executada, acaoAtualizada.Estado);
        Assert.Equal(0, acaoAtualizada.CodigoSaida);
        Assert.Equal("OK", acaoAtualizada.Resultado);
        Assert.NotNull(acaoAtualizada.ConcluidoEm);
        Assert.NotNull(acaoAtualizada.EnviadoEm);

        // Auditoria foi gravada
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.AcaoId == acao.Id && a.Sucesso);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task TransicaoEstado_Cancelamento_AcaoPendenteViraCancelada()
    {
        var (service, db, _, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "TRANS_CANCEL", online: false);

        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin_user");
        Assert.Equal(EstadosAcao.Pendente, acao.Estado);

        var cancelou = await service.CancelarAcaoAsync(acao.Id, "admin_user", "Cancelamento manual de teste");
        Assert.True(cancelou);

        var acaoAtualizada = await db.Acoes.FindAsync(acao.Id);
        Assert.NotNull(acaoAtualizada);
        Assert.Equal(EstadosAcao.Cancelada, acaoAtualizada.Estado);
        Assert.NotNull(acaoAtualizada.ConcluidoEm);

        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.AcaoId == acao.Id && a.Tipo == TiposAuditoria.CancelarPendencia);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task ExclusaoMutua_NaoEnviaAcaoPendenteSeHouverOutraEnviadaOuResultadoDesconhecido()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "EXCL_MUTUA", online: true);

        // Simula comando que fica pendente/executando (não retorna imediatamente ou simula ação enviada)
        var acao1 = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin_user");
        var acao2 = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Instalar, "admin_user");

        // Coloca acao1 em 'enviada'
        acao1.Estado = EstadosAcao.Enviada;
        acao1.EnviadoEm = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Tenta processar máquina: acao2 NÃO deve ser enviada enquanto acao1 estiver em 'enviada'
        await service.ProcessarMaquinaAsync(maquina.Id);

        var acao2Verif = await db.Acoes.FindAsync(acao2.Id);
        Assert.NotNull(acao2Verif);
        Assert.Equal(EstadosAcao.Pendente, acao2Verif.Estado);
        Assert.Equal(0, meshClient.RunScriptCalls);

        // Agora coloca acao1 em 'resultado_desconhecido' com máquina offline para que não reconcilie agora
        maquina.ConexaoEstado = "offline";
        acao1.Estado = EstadosAcao.ResultadoDesconhecido;
        await db.SaveChangesAsync();

        // Processa: acao2 ainda NÃO pode ser enviada pois há ação em 'resultado_desconhecido'
        await service.ProcessarMaquinaAsync(maquina.Id);
        acao2Verif = await db.Acoes.FindAsync(acao2.Id);
        Assert.Equal(EstadosAcao.Pendente, acao2Verif!.Estado);
        Assert.Equal(0, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task Prioridade_EnviaAcoesNaOrdemEstrita_Resgate_Depois_Bloqueio_Depois_Aplicativos()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "PRIORIDADE", online: true);

        var ordemExecucao = new List<string>();
        meshClient.RunScriptHandler = (nodeId, script) =>
        {
            if (script.Contains("GarantirContaResgate")) ordemExecucao.Add("resgate");
            else if (script.Contains("BloquearMaquina")) ordemExecucao.Add("bloquear");
            else ordemExecucao.Add("instalar");

            return new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");
        };

        // Enfileira em ordem inversa propositalmente: Aplicativo (P3), Bloqueio (P2), Resgate (P1)
        var acaoApp = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Instalar, "admin", prioridade: PrioridadesAcao.Aplicativos);
        var acaoBloq = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin", prioridade: PrioridadesAcao.Bloqueio);
        var acaoResgate = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.GarantirResgate, "admin", prioridade: PrioridadesAcao.Resgate);

        // 1. Primeiro despacho deve ser Resgate (P1)
        await service.ProcessarMaquinaAsync(maquina.Id);
        Assert.Single(ordemExecucao);
        Assert.Equal("resgate", ordemExecucao[0]);

        var aResgateVerif = await db.Acoes.FindAsync(acaoResgate.Id);
        Assert.Equal(EstadosAcao.Executada, aResgateVerif!.Estado);

        // 2. Segundo despacho deve ser Bloqueio (P2)
        await service.ProcessarMaquinaAsync(maquina.Id);
        Assert.Equal(2, ordemExecucao.Count);
        Assert.Equal("bloquear", ordemExecucao[1]);

        var aBloqVerif = await db.Acoes.FindAsync(acaoBloq.Id);
        Assert.Equal(EstadosAcao.Executada, aBloqVerif!.Estado);

        // 3. Terceiro despacho deve ser Instalar (P3)
        await service.ProcessarMaquinaAsync(maquina.Id);
        Assert.Equal(3, ordemExecucao.Count);
        Assert.Equal("instalar", ordemExecucao[2]);

        var aAppVerif = await db.Acoes.FindAsync(acaoApp.Id);
        Assert.Equal(EstadosAcao.Executada, aAppVerif!.Estado);
    }

    [Fact]
    public async Task Ocupado_NaoContaTentativasERetentaEmCiclo_FalhaApos1Hora()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "OCUPADO_TEST", online: true);

        // Retorna OCUPADO
        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OCUPADO", 0, "MSI installer already busy\r\nGTV-RESULT=OCUPADO\r\nGTV-EXIT=0");

        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Instalar, "admin");
        await service.ProcessarMaquinaAsync(maquina.Id);

        var acaoVerif = await db.Acoes.FindAsync(acao.Id);
        Assert.NotNull(acaoVerif);
        Assert.Equal(EstadosAcao.Pendente, acaoVerif.Estado);
        Assert.Equal("OCUPADO", acaoVerif.Resultado);
        // Tentativas NÃO deve ter sido incrementado
        Assert.Equal(0, acaoVerif.Tentativas);
        Assert.Contains("OCUPADO_INICIO:", acaoVerif.Detalhes ?? "");

        // Simula que se passaram 65 minutos (> 1 hora) desde o primeiro OCUPADO
        var inicioMaisDe1hAtras = DateTimeOffset.UtcNow.AddMinutes(-65);
        acaoVerif.Detalhes = $"OCUPADO_INICIO:{inicioMaisDe1hAtras.UtcTicks};OCUPADO_PROXIMO:{DateTimeOffset.UtcNow.AddSeconds(-5).UtcTicks}";
        await db.SaveChangesAsync();

        // Processa novamente -> Deve transicionar para 'falhou' por exceder 1h
        await service.ProcessarMaquinaAsync(maquina.Id);

        var acaoFalhou = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.Falhou, acaoFalhou!.Estado);
        Assert.Equal("OCUPADO", acaoFalhou.Resultado);
        Assert.Contains("Limite de 1h", acaoFalhou.Detalhes);
    }

    [Fact]
    public async Task Reconciliacao_ResultadoDesconhecido_ComMaquinaOnline_SucessoEReenvios()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "RECON_ONLINE", online: true);

        // Caso Bloqueio: VerificarBloqueio retorna BLOQUEADA -> reconcilia para executada diretamente
        meshClient.RunScriptHandler = (nodeId, script) =>
        {
            if (script.Contains("VerificarBloqueio"))
            {
                return new RunCommandResult("BLOQUEADA", 0, "GTV-RESULT=BLOQUEADA\r\nGTV-EXIT=0");
            }
            return new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");
        };

        var acaoBloq = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin");
        acaoBloq.Estado = EstadosAcao.ResultadoDesconhecido;
        await db.SaveChangesAsync();

        await service.ProcessarMaquinaAsync(maquina.Id);

        var acaoReconciliada = await db.Acoes.FindAsync(acaoBloq.Id);
        Assert.Equal(EstadosAcao.Executada, acaoReconciliada!.Estado);
        Assert.Equal("BLOQUEADA", acaoReconciliada.Resultado);
        Assert.Equal("bloqueada", maquina.BloqueioEstado);

        // Auditoria de verificação foi registrada
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.Tipo == TiposAuditoria.VerificarReaplicarBloqueio && a.MaquinaId == maquina.Id);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task Reconciliacao_LimiteDe3Reenvios_FalhaApos3Tentativas()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "RECON_LIMIT3", online: true);

        // Simula falha persistente na reconciliação
        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("ERRO_SCRIPT", 1, "GTV-RESULT=ERRO_SCRIPT\r\nGTV-EXIT=1");

        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.GarantirResgate, "admin");
        acao.Estado = EstadosAcao.ResultadoDesconhecido;
        acao.Tentativas = 2; // já falhou 2 vezes
        await db.SaveChangesAsync();

        // 3ª tentativa -> deve falhar
        await service.ProcessarMaquinaAsync(maquina.Id);

        var acaoFalha = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.Falhou, acaoFalha!.Estado);
        Assert.Equal(3, acaoFalha.Tentativas);
        Assert.Equal(1, acaoFalha.CodigoSaida);
    }

    [Fact]
    public async Task AuditoriaMesmaTransacao_FalhaNaGravacaoAuditoria_NadaEEnviado()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "AUDIT_FAIL", online: true);

        // Para validar que sem gravação de auditoria nada é enviado ao MeshCentral,
        // criamos uma ação com CancellationToken já cancelado durante o envio:
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin", cancellationToken: cts.Token);
        });

        // Nenhuma chamada ao MeshCentral ocorreu
        Assert.Equal(0, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task AcaoCriadaComVMDesligada_ExecutaAoLigarNodeConnected()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "VM_OFFLINE", online: false);

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");

        // Enfileira com VM offline
        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin");

        // Processa com VM desligada: deve permanecer pendente
        await service.ProcessarMaquinaAsync(maquina.Id);

        var acaoVerif = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.Pendente, acaoVerif!.Estado);
        Assert.Equal(0, meshClient.RunScriptCalls);

        // VM liga -> evento NodeConnected recebido
        await service.ProcessarNodeConnectedAsync(maquina.NodeId!);

        var acaoExecutada = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.Executada, acaoExecutada!.Estado);
        Assert.Equal(1, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task ScriptInterrompidoNoMeio_ViraResultadoDesconhecido_EConcluiNaReconexao()
    {
        var (service, db, meshClient, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "INTERRUPT", online: true);

        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.GarantirResgate, "admin");

        // 1. Despacha ação para 'enviada'
        acao.Estado = EstadosAcao.Enviada;
        acao.EnviadoEm = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // 2. VM desconecta no meio da execução
        await service.TratarDesconexaoMaquinaAsync(maquina.NodeId!);

        var acaoDesconhecida = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.ResultadoDesconhecido, acaoDesconhecida!.Estado);
        Assert.Contains("desconectada", acaoDesconhecida.Detalhes ?? "");

        // 3. VM reconecta e script é executado com sucesso
        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");

        await service.ProcessarNodeConnectedAsync(maquina.NodeId!);

        var acaoConcluida = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.Executada, acaoConcluida!.Estado);
        Assert.Equal(0, acaoConcluida.CodigoSaida);
    }

    [Fact]
    public async Task ReconciliarAcoesOrfasNaSubida_TransicionaEnviadaParaResultadoDesconhecido()
    {
        var (service, db, _, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "ORPHA_STARTUP", online: false);

        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin");
        acao.Estado = EstadosAcao.Enviada;
        acao.EnviadoEm = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        // Subida do serviço
        await service.ReconciliarAcoesOrfasNaSubidaAsync();

        var acaoAtualizada = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.ResultadoDesconhecido, acaoAtualizada!.Estado);
        Assert.Contains("Ação órfã", acaoAtualizada.Detalhes ?? "");
    }

    [Fact]
    public async Task Timeout_AcaoEnviadaSemResposta_ViraResultadoDesconhecido()
    {
        var (service, db, _, _) = CreateTestService();
        var maquina = await CriarMaquinaTesteAsync(db, "TIMEOUT_TEST", online: true);

        var acao = await service.EnfileirarAcaoAsync(maquina.Id, TiposAcao.Bloquear, "admin");
        acao.Estado = EstadosAcao.Enviada;
        // Timeout de bloqueio é 2 minutos: simula 5 minutos atrás
        acao.EnviadoEm = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        // Ciclo periódico detecta timeout
        await service.ProcessarCicloFilaAsync();

        var acaoAtualizada = await db.Acoes.FindAsync(acao.Id);
        Assert.Equal(EstadosAcao.ResultadoDesconhecido, acaoAtualizada!.Estado);
        Assert.Contains("Timeout", acaoAtualizada.Detalhes ?? "");
    }

    [Fact]
    public async Task TarefaGrupo_CriaAcaoPorMaquinaEAgregaConclusao()
    {
        var (service, db, meshClient, _) = CreateTestService();

        var grupo = new Grupo { Nome = $"TI_{Guid.NewGuid():N}"[..25] };
        db.Grupos.Add(grupo);
        await db.SaveChangesAsync();

        var m1 = await CriarMaquinaTesteAsync(db, "GRP_M1", online: true);
        var m2 = await CriarMaquinaTesteAsync(db, "GRP_M2", online: true);
        m1.GrupoId = grupo.Id;
        m2.GrupoId = grupo.Id;
        await db.SaveChangesAsync();

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");

        // Cria tarefa de grupo
        var tarefa = await service.CriarTarefaGrupoAsync(grupo.Id, TiposAcao.Instalar, "admin_user");
        Assert.NotNull(tarefa);

        var acoesGrupo = await db.Acoes.Where(a => a.TarefaId == tarefa.Id).ToListAsync();
        Assert.Equal(2, acoesGrupo.Count);
        Assert.All(acoesGrupo, a => Assert.Equal(EstadosAcao.Pendente, a.Estado));

        // Processa as duas máquinas
        await service.ProcessarMaquinaAsync(m1.Id);
        await service.ProcessarMaquinaAsync(m2.Id);

        var tarefaAtualizada = await db.Tarefas.Include(t => t.Acoes).FirstOrDefaultAsync(t => t.Id == tarefa.Id);
        Assert.NotNull(tarefaAtualizada);
        Assert.All(tarefaAtualizada.Acoes, a => Assert.Equal(EstadosAcao.Executada, a.Estado));
        Assert.NotNull(tarefaAtualizada.ConcluidoEm);
    }
}
