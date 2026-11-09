using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public class BloqueioSenhaResgateJ6Tests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private static (
        BloqueioService bloqueioService,
        FilaAcoesService filaService,
        GtViewerDbContext db,
        MockMeshCentralClient meshClient,
        IRescuePasswordService rescueService,
        ITotpService totpService,
        IAuditoriaService auditoriaService) CreateTestServices()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var meshClient = new MockMeshCentralClient();
        var scriptProvider = new AgentScriptProvider();
        var protection = new EphemeralDataProtectionProvider();
        var rescueService = new RescuePasswordService(protection);
        var totpService = new TotpService(protection);
        var auditoriaService = new AuditoriaService(db);
        var options = Options.Create(new MeshCentralOptions { ServerUrl = "https://gtviewer.local" });

        var filaService = new FilaAcoesService(
            db,
            meshClient,
            scriptProvider,
            rescueService,
            options,
            NullLogger<FilaAcoesService>.Instance);

        var bloqueioService = new BloqueioService(
            db,
            filaService,
            rescueService,
            totpService,
            auditoriaService,
            meshClient,
            NullLogger<BloqueioService>.Instance);

        return (bloqueioService, filaService, db, meshClient, rescueService, totpService, auditoriaService);
    }

    private static async Task<Tecnico> CriarTecnicoTesteAsync(
        GtViewerDbContext db,
        ITotpService totpService,
        string permissoes = $"{Permissoes.Bloquear},{Permissoes.RevelarSenhaResgate}",
        string? username = null)
    {
        var user = username ?? $"tec_{Guid.NewGuid():N}"[..20];
        var plainSecret = totpService.GenerateSecret();
        var encryptedSecret = totpService.EncryptSecret(plainSecret);

        var tecnico = new Tecnico
        {
            UsuarioDominio = user,
            NomeExibicao = $"Técnico {user}",
            Permissoes = permissoes,
            Ativo = true,
            TotpConfigurado = true,
            TotpSecretCifrado = encryptedSecret,
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Tecnicos.Add(tecnico);
        await db.SaveChangesAsync();
        return tecnico;
    }

    private static async Task<Maquina> CriarMaquinaTesteAsync(
        GtViewerDbContext db,
        IRescuePasswordService rescueService,
        string prefix = "M_TEST",
        bool online = true,
        bool temSenhaConfirmada = true,
        string bloqueioEstado = "desbloqueada")
    {
        var serial = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var nodeId = $"node//test_{Guid.NewGuid():N}";
        var plainPwd = rescueService.GeneratePassword();

        var maquina = new Maquina
        {
            NumeroSerie = serial,
            NodeId = nodeId,
            Hostname = $"HOST-{serial}",
            ConexaoEstado = online ? "online" : "offline",
            BloqueioEstado = bloqueioEstado,
            SenhaResgateAtualCifrada = temSenhaConfirmada ? rescueService.EncryptPassword(plainPwd) : null,
            SenhaResgateRevelada = false,
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = online ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();
        return maquina;
    }

    [Fact]
    public async Task Bloquear_ComPermissaoEMotivo_GeraBloqueioPendenteEAuditoria()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "LOCK_OK", online: false);

        await bloqueioService.SolicitarBloqueioAsync(maquina.Id, tecnico.UsuarioDominio, "Notebook esquecido no táxi");

        var mAtualizada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.NotNull(mAtualizada);
        Assert.Equal("bloqueio_pendente", mAtualizada.BloqueioEstado);
        Assert.Equal("Notebook esquecido no táxi", mAtualizada.MensagemBloqueio);

        // Ação na fila
        var acaoBloq = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.Bloquear);
        Assert.NotNull(acaoBloq);
        Assert.Equal(EstadosAcao.Pendente, acaoBloq.Estado);
        Assert.Equal(PrioridadesAcao.Bloqueio, acaoBloq.Prioridade);

        // Auditoria registrada
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.AcaoId == acaoBloq.Id && a.Tipo == TiposAuditoria.Bloquear);
        Assert.NotNull(audit);
        Assert.Equal(tecnico.UsuarioDominio, audit.Ator);
    }

    [Fact]
    public async Task Bloquear_SemPermissao_Retorna403()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        // Técnico SEM permissão 'bloquear'
        var tecnico = await CriarTecnicoTesteAsync(db, totpService, permissoes: Permissoes.SuporteRemoto);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "LOCK_403");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bloqueioService.SolicitarBloqueioAsync(maquina.Id, tecnico.UsuarioDominio, "Motivo teste"));
    }

    [Fact]
    public async Task Bloquear_SemMotivo_FalhaComArgumentException()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "LOCK_NOMOT");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            bloqueioService.SolicitarBloqueioAsync(maquina.Id, tecnico.UsuarioDominio, "   "));
    }

    [Fact]
    public async Task Bloquear_SemContaResgateConfirmada_RecusaBloqueio()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        // Máquina SEM senha atual de resgate confirmada
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "LOCK_NOPWD", temSenhaConfirmada: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bloqueioService.SolicitarBloqueioAsync(maquina.Id, tecnico.UsuarioDominio, "Colaborador desligado"));

        Assert.Contains("senha da conta de resgate", ex.Message);
    }

    [Fact]
    public async Task BloquearPendente_AplicadoAoReconectar_ExecutaScriptEAtualizaParaBloqueada()
    {
        var (bloqueioService, filaService, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "LOCK_CONNECT", online: false);

        await bloqueioService.SolicitarBloqueioAsync(maquina.Id, tecnico.UsuarioDominio, "Bloqueio preventivo");

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("BLOQUEADA", 0, "GTV-RESULT=BLOQUEADA\r\nGTV-EXIT=0");

        // VM liga -> evento NodeConnected recebido
        await filaService.ProcessarNodeConnectedAsync(maquina.NodeId!);

        var mAtualizada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.NotNull(mAtualizada);
        Assert.Equal("bloqueada", mAtualizada.BloqueioEstado);

        var acaoBloq = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.Bloquear);
        Assert.NotNull(acaoBloq);
        Assert.Equal(EstadosAcao.Executada, acaoBloq.Estado);
        Assert.Equal("BLOQUEADA", acaoBloq.Resultado);
    }

    [Fact]
    public async Task Desbloquear_ComBloqueioPendente_CancelaAcaoEVoltaParaDesbloqueada()
    {
        var (bloqueioService, _, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "UNLOCK_PEND", online: false);

        await bloqueioService.SolicitarBloqueioAsync(maquina.Id, tecnico.UsuarioDominio, "Bloqueio cancelável");

        // Solicita desbloqueio com a ação de bloqueio ainda pendente
        await bloqueioService.SolicitarDesbloqueioAsync(maquina.Id, tecnico.UsuarioDominio);

        var mAtualizada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.NotNull(mAtualizada);
        Assert.Equal("desbloqueada", mAtualizada.BloqueioEstado);
        Assert.Null(mAtualizada.MensagemBloqueio);

        var acaoBloq = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.Bloquear);
        Assert.NotNull(acaoBloq);
        Assert.Equal(EstadosAcao.Cancelada, acaoBloq.Estado);

        // Nada foi despachado para a VM
        Assert.Equal(0, meshClient.RunScriptCalls);
    }

    [Fact]
    public async Task Desbloquear_ComBloqueioEnviada_AplicaEmSeguida()
    {
        var (bloqueioService, filaService, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "UNLOCK_SENT", online: true);

        await bloqueioService.SolicitarBloqueioAsync(maquina.Id, tecnico.UsuarioDominio, "Bloqueio em andamento");

        var acaoBloq = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.Bloquear);
        acaoBloq!.Estado = EstadosAcao.Enviada;
        acaoBloq.EnviadoEm = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Solicita desbloqueio enquanto bloqueio está em 'enviada'
        await bloqueioService.SolicitarDesbloqueioAsync(maquina.Id, tecnico.UsuarioDominio);

        var mAtualizada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.Equal("desbloqueio_pendente", mAtualizada!.BloqueioEstado);

        var acaoDesbloq = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.Desbloquear);
        Assert.NotNull(acaoDesbloq);
        Assert.Equal(EstadosAcao.Pendente, acaoDesbloq.Estado);

        // Bloqueio conclui
        acaoBloq.Estado = EstadosAcao.Executada;
        await db.SaveChangesAsync();

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("DESBLOQUEADA", 0, "GTV-RESULT=DESBLOQUEADA\r\nGTV-EXIT=0");

        // Fila processa máquina -> desbloqueio é despachado em seguida
        await filaService.ProcessarMaquinaAsync(maquina.Id);

        var acaoDesbloqVerif = await db.Acoes.FindAsync(acaoDesbloq.Id);
        Assert.Equal(EstadosAcao.Executada, acaoDesbloqVerif!.Estado);

        var mFinal = await db.Maquinas.FindAsync(maquina.Id);
        Assert.Equal("desbloqueada", mFinal!.BloqueioEstado);
    }

    [Fact]
    public async Task Desbloquear_APartirDeFalha_AplicaDesbloqueioERestaura()
    {
        var (bloqueioService, filaService, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "UNLOCK_FAIL", online: true, bloqueioEstado: "falha");

        await bloqueioService.SolicitarDesbloqueioAsync(maquina.Id, tecnico.UsuarioDominio);

        var mAtualizada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.Equal("desbloqueio_pendente", mAtualizada!.BloqueioEstado);

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("DESBLOQUEADA", 0, "GTV-RESULT=DESBLOQUEADA\r\nGTV-EXIT=0");

        await filaService.ProcessarMaquinaAsync(maquina.Id);

        var mFinal = await db.Maquinas.FindAsync(maquina.Id);
        Assert.Equal("desbloqueada", mFinal!.BloqueioEstado);
    }

    [Fact]
    public async Task RevelarSenha_ValidaCondicoesCompletas_OfflineMais5Min_NovoTOTP_AuditadoSemSenha()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);

        // Máquina bloqueada e offline há 15 minutos (> 5 min)
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "REV_OK", online: false, bloqueioEstado: "bloqueada");
        maquina.UltimoContatoEm = DateTimeOffset.UtcNow.AddMinutes(-15);
        await db.SaveChangesAsync();

        var secret = totpService.DecryptSecret(tecnico.TotpSecretCifrado!);
        var totpCode = TotpService.ComputeCurrentTotpCode(secret);

        var senhaRevelada = await bloqueioService.RevelarSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio, totpCode);

        Assert.NotNull(senhaRevelada);
        Assert.Equal(20, senhaRevelada.Length);

        var mAtualizada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.True(mAtualizada!.SenhaResgateRevelada);

        // Auditoria registrada SEM segredo
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.Tipo == TiposAuditoria.RevelarSenhaResgate && a.MaquinaId == maquina.Id);
        Assert.NotNull(audit);
        Assert.True(audit.Sucesso);
        Assert.DoesNotContain(senhaRevelada, audit.DetalhesJson ?? "");
    }

    [Fact]
    public async Task RevelarSenha_RecusaSeOfflineHaMenosDe5MinOuOnline()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);

        // Máquina online
        var maquinaOnline = await CriarMaquinaTesteAsync(db, rescueService, "REV_ONLINE", online: true, bloqueioEstado: "bloqueada");
        var secret = totpService.DecryptSecret(tecnico.TotpSecretCifrado!);
        var totpCode = TotpService.ComputeCurrentTotpCode(secret);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bloqueioService.RevelarSenhaResgateAsync(maquinaOnline.Id, tecnico.UsuarioDominio, totpCode));

        // Máquina offline há apenas 2 minutos (< 5 min)
        var maquinaOfflineRecente = await CriarMaquinaTesteAsync(db, rescueService, "REV_REC", online: false, bloqueioEstado: "bloqueada");
        maquinaOfflineRecente.UltimoContatoEm = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bloqueioService.RevelarSenhaResgateAsync(maquinaOfflineRecente.Id, tecnico.UsuarioDominio, totpCode));
    }

    [Fact]
    public async Task RevelarSenha_IndisponivelComMeshCentralCaido()
    {
        var (bloqueioService, _, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "REV_MESHOFF", online: false, bloqueioEstado: "bloqueada");
        maquina.UltimoContatoEm = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        // Derruba o MeshCentral
        meshClient.IsConnected = false;

        var secret = totpService.DecryptSecret(tecnico.TotpSecretCifrado!);
        var totpCode = TotpService.ComputeCurrentTotpCode(secret);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bloqueioService.RevelarSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio, totpCode));

        Assert.Contains("MeshCentral", ex.Message);
    }

    [Fact]
    public async Task RevelarSenha_RecusaComCodigoTotpInvalidoOuReutilizado()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "REV_TOTP", online: false, bloqueioEstado: "bloqueada");
        maquina.UltimoContatoEm = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        // 1. Código errado
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bloqueioService.RevelarSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio, "999999"));

        // 2. Código válido na primeira vez
        var secret = totpService.DecryptSecret(tecnico.TotpSecretCifrado!);
        var validCode = TotpService.ComputeCurrentTotpCode(secret);

        await bloqueioService.RevelarSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio, validCode);

        // 3. Reuso do mesmo código TOTP imediatamente -> Recusa (anti-replay)
        var exReuso = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bloqueioService.RevelarSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio, validCode));

        Assert.Contains("autenticador", exReuso.Message);
    }

    [Fact]
    public async Task SenhaRevelada_TrocadaNaConexaoSeguinte_EncerraSessaoResgateMesmoBloqueada()
    {
        var (bloqueioService, filaService, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "ROT_ON_CONN", online: false, bloqueioEstado: "bloqueada");
        maquina.UltimoContatoEm = DateTimeOffset.UtcNow.AddMinutes(-15);
        await db.SaveChangesAsync();

        // Revela a senha
        var secret = totpService.DecryptSecret(tecnico.TotpSecretCifrado!);
        var totpCode = TotpService.ComputeCurrentTotpCode(secret);
        var senhaRevelada = await bloqueioService.RevelarSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio, totpCode);

        var mRevelada = await db.Maquinas.FindAsync(maquina.Id);
        Assert.True(mRevelada!.SenhaResgateRevelada);

        string? scriptExecutado = null;
        meshClient.RunScriptHandler = (nodeId, script) =>
        {
            scriptExecutado = script;
            return new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");
        };

        // Máquina reconecta -> evento NodeConnected
        await filaService.ProcessarNodeConnectedAsync(maquina.NodeId!);

        // Troca de senha deve ter sido despachada e conter flag -Revealed
        Assert.NotNull(scriptExecutado);
        Assert.Contains("$Revealed = $true", scriptExecutado);
        Assert.Contains("GarantirContaResgate", scriptExecutado);

        var mFinal = await db.Maquinas.FindAsync(maquina.Id);
        Assert.False(mFinal!.SenhaResgateRevelada); // Marca de revelada foi limpa após troca
        Assert.Equal("bloqueada", mFinal.BloqueioEstado); // Continua bloqueada!
        Assert.Null(mFinal.SenhaResgateCandidataCifrada); // Candidata virou atual

        var novaSenhaAtual = rescueService.DecryptPassword(mFinal.SenhaResgateAtualCifrada!);
        Assert.NotEqual(senhaRevelada, novaSenhaAtual); // Senha trocada com sucesso!
    }

    [Fact]
    public async Task QuedaNoMeioDaTroca_MantemCandidataEAtualUtilizaveis_ReaplicaNaReconciliacao()
    {
        var (bloqueioService, filaService, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "DROP_MID", online: true, bloqueioEstado: "bloqueada");

        // Inicia troca de senha
        await bloqueioService.AgendarTrocaSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio);

        var mComCandidata = await db.Maquinas.FindAsync(maquina.Id);
        Assert.NotNull(mComCandidata!.SenhaResgateAtualCifrada);
        Assert.NotNull(mComCandidata.SenhaResgateCandidataCifrada);

        // Ambas as senhas decifram com sucesso (utilizáveis se precisar revelar)
        var senhaAtual = rescueService.DecryptPassword(mComCandidata.SenhaResgateAtualCifrada!);
        var senhaCandidata = rescueService.DecryptPassword(mComCandidata.SenhaResgateCandidataCifrada!);
        Assert.NotEqual(senhaAtual, senhaCandidata);

        // Simula queda de conexão no meio da execução (vira resultado_desconhecido)
        var acaoTroca = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.TrocarResgate);
        acaoTroca!.Estado = EstadosAcao.Enviada;
        acaoTroca.EnviadoEm = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await filaService.TratarDesconexaoMaquinaAsync(maquina.NodeId!);

        var acaoDesconhecida = await db.Acoes.FindAsync(acaoTroca.Id);
        Assert.Equal(EstadosAcao.ResultadoDesconhecido, acaoDesconhecida!.Estado);

        // Reconecta e reconcilia
        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OK", 0, "GTV-RESULT=OK\r\nGTV-EXIT=0");

        await filaService.ProcessarNodeConnectedAsync(maquina.NodeId!);

        var mConcluida = await db.Maquinas.FindAsync(maquina.Id);
        Assert.Null(mConcluida!.SenhaResgateCandidataCifrada);
        Assert.Equal(senhaCandidata, rescueService.DecryptPassword(mConcluida.SenhaResgateAtualCifrada!));
    }

    [Fact]
    public async Task NovaTrocaDeSenha_RecusadaComCandidataPendente()
    {
        var (bloqueioService, _, db, _, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "CAND_PEND", online: true);

        await bloqueioService.AgendarTrocaSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio);

        // Tentar agendar outra troca enquanto candidata não foi confirmada -> deve ser recusado
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bloqueioService.AgendarTrocaSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio));

        Assert.Contains("candidata", ex.Message);
    }

    [Fact]
    public async Task TokenAgente_ValidoEntregaSenha_ReutilizadoOuExpiradoRecusado()
    {
        var (bloqueioService, filaService, db, meshClient, rescueService, _, _) = CreateTestServices();
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "TOKEN_TEST", online: true);

        // Gera ação que emite token agente
        var acao = await filaService.EnfileirarAcaoAsync(maquina.Id, TiposAcao.GarantirResgate, "sistema");
        var tokenRaw = Guid.NewGuid().ToString("N");

        using (var sha = SHA256.Create())
        {
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(tokenRaw));
            acao.TokenAgenteHash = Convert.ToHexStringLower(hashBytes);
        }
        acao.TokenAgenteExpiraEm = DateTimeOffset.UtcNow.AddMinutes(5);
        acao.TokenAgenteUsado = false;
        await db.SaveChangesAsync();

        // 1. Token válido -> retorna senha em texto plano
        var senhaObtida = await bloqueioService.ObterSenhaAgentePorTokenAsync(tokenRaw);
        Assert.NotNull(senhaObtida);

        // 2. Token reutilizado -> recusado (retorna null)
        var senhaReuso = await bloqueioService.ObterSenhaAgentePorTokenAsync(tokenRaw);
        Assert.Null(senhaReuso);

        // 3. Token expirado -> recusado (retorna null)
        var tokenExpiradoRaw = Guid.NewGuid().ToString("N");
        var acao2 = await filaService.EnfileirarAcaoAsync(maquina.Id, TiposAcao.GarantirResgate, "sistema");
        using (var sha = SHA256.Create())
        {
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(tokenExpiradoRaw));
            acao2.TokenAgenteHash = Convert.ToHexStringLower(hashBytes);
        }
        acao2.TokenAgenteExpiraEm = DateTimeOffset.UtcNow.AddMinutes(-5); // expirado
        acao2.TokenAgenteUsado = false;
        await db.SaveChangesAsync();

        var senhaExpirada = await bloqueioService.ObterSenhaAgentePorTokenAsync(tokenExpiradoRaw);
        Assert.Null(senhaExpirada);
    }

    [Fact]
    public async Task TrocaDeSenhaEmFalhou_DisparaRedefinirContaResgate()
    {
        var (bloqueioService, filaService, db, meshClient, rescueService, totpService, _) = CreateTestServices();
        var tecnico = await CriarTecnicoTesteAsync(db, totpService);
        var maquina = await CriarMaquinaTesteAsync(db, rescueService, "FAIL_REDEF", online: true);

        await bloqueioService.AgendarTrocaSenhaResgateAsync(maquina.Id, tecnico.UsuarioDominio);

        // Simula falha contínua do agente (GTV-EXIT=1)
        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("FALHA_CONTA", 1, "GTV-RESULT=FALHA_CONTA\r\nGTV-EXIT=1");

        // 3 tentativas falhas
        await filaService.ProcessarMaquinaAsync(maquina.Id); // tentativa 1 -> resultado_desconhecido
        await filaService.ProcessarMaquinaAsync(maquina.Id); // tentativa 2 -> resultado_desconhecido
        await filaService.ProcessarMaquinaAsync(maquina.Id); // tentativa 3 -> falhou

        var acaoTroca = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.TrocarResgate);
        Assert.Equal(EstadosAcao.Falhou, acaoTroca!.Estado);

        // Disparo automático de redefinir_resgate (Prioridade 1)
        var acaoRedefinir = await db.Acoes.FirstOrDefaultAsync(a => a.MaquinaId == maquina.Id && a.Tipo == TiposAcao.RedefinirResgate);
        Assert.NotNull(acaoRedefinir);
        Assert.True(acaoRedefinir.Estado is EstadosAcao.Pendente or EstadosAcao.ResultadoDesconhecido);
        Assert.Equal(PrioridadesAcao.Resgate, acaoRedefinir.Prioridade);
    }
}
