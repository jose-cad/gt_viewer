using System.Text.Json;
using System.Text.Json.Nodes;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.Inventario;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GtViewer.Tests;

public class InventarioJ2J3Tests
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private static (InventarioService service, GtViewerDbContext db, IRescuePasswordService rescueService) CreateTestService()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var protection = new EphemeralDataProtectionProvider();
        var rescueService = new RescuePasswordService(protection);
        var auditoria = new AuditoriaService(db);
        var service = new InventarioService(db, auditoria, rescueService, NullLogger<InventarioService>.Instance);
        return (service, db, rescueService);
    }

    [Fact]
    public async Task ProcessNodeUpdate_SerieNova_CadastraMaquina_AgendaGarantirResgate_CriaAuditoria()
    {
        var (service, db, rescueService) = CreateTestService();
        var uniqueSerial = "BRG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var nodeId = "node//" + Guid.NewGuid().ToString("N")[..8];

        var payload = new JsonObject
        {
            ["name"] = "DEV-NOTEBOOK-01",
            ["conn"] = 1,
            ["osdesc"] = "Microsoft Windows 11 Pro 64-bit",
            ["ip"] = "192.168.1.100",
            ["hardware"] = new JsonObject
            {
                ["identifiers"] = new JsonObject
                {
                    ["bios_serial"] = uniqueSerial,
                    ["product_name"] = "Latitude 5420",
                    ["chassis_manufacturer"] = "Dell Inc."
                },
                ["windows"] = new JsonObject
                {
                    ["osinfo"] = new JsonObject
                    {
                        ["OSArchitecture"] = "64-bit"
                    }
                }
            },
            ["av"] = new JsonArray
            {
                new JsonObject
                {
                    ["product"] = "Windows Defender",
                    ["enabled"] = true,
                    ["updated"] = true
                }
            },
            ["users"] = new JsonArray { "usuario_teste" }
        };

        await service.ProcessNodeUpdateAsync(nodeId, payload);

        // Verifica máquina cadastrada no banco
        var maquina = await db.Maquinas
            .Include(m => m.Acoes)
            .Include(m => m.HistoricoUsuarios)
            .FirstOrDefaultAsync(m => m.NumeroSerie == uniqueSerial);

        Assert.NotNull(maquina);
        Assert.Equal(nodeId, maquina.NodeId);
        Assert.Equal("DEV-NOTEBOOK-01", maquina.Hostname);
        Assert.Equal("Latitude 5420", maquina.Modelo);
        Assert.Equal("Dell Inc.", maquina.Fabricante);
        Assert.Equal("online", maquina.ConexaoEstado);
        Assert.Equal("desbloqueada", maquina.BloqueioEstado);
        Assert.NotNull(maquina.SenhaResgateCandidataCifrada);

        // Confere senha decifrável
        var plainPassword = rescueService.DecryptPassword(maquina.SenhaResgateCandidataCifrada);
        Assert.Equal(20, plainPassword.Length);

        // Confere ação garantir_resgate pendente (prioridade 1)
        var acaoResgate = maquina.Acoes.FirstOrDefault(a => a.Tipo == "garantir_resgate");
        Assert.NotNull(acaoResgate);
        Assert.Equal(1, acaoResgate.Prioridade);
        Assert.Equal("pendente", acaoResgate.Estado);
        Assert.Equal("sistema", acaoResgate.Ator);

        // Confere linha de auditoria
        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.Alvo == uniqueSerial && a.Tipo == TiposAuditoria.GarantirContaResgate);
        Assert.NotNull(audit);
        Assert.True(audit.Sucesso);
        Assert.Equal("sistema", audit.Ator);

        // Confere histórico de usuário inicial
        var hist = maquina.HistoricoUsuarios.FirstOrDefault(h => h.Usuario == "usuario_teste");
        Assert.NotNull(hist);
        Assert.Null(hist.FimEm);
    }

    [Fact]
    public async Task ProcessNodeUpdate_SerieConhecida_AgenteAntigoOffline_ColocaEmAprovacao_NaoEnviaAcao()
    {
        var (service, db, _) = CreateTestService();
        var uniqueSerial = "BRG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var oldNodeId = "node//old_" + Guid.NewGuid().ToString("N")[..6];
        var newNodeId = "node//new_" + Guid.NewGuid().ToString("N")[..6];

        // 1. Cadastra máquina inicial com agente antigo
        var maquina = new Maquina
        {
            NumeroSerie = uniqueSerial,
            NodeId = oldNodeId,
            Hostname = "OLD-HOST",
            ConexaoEstado = "offline",
            BloqueioEstado = "desbloqueada",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        // 2. Novo agente conecta informando a MESMA série
        var payloadNovoAgente = new JsonObject
        {
            ["name"] = "NEW-REFORMATTED-HOST",
            ["conn"] = 1,
            ["ip"] = "192.168.1.150",
            ["hardware"] = new JsonObject
            {
                ["identifiers"] = new JsonObject
                {
                    ["bios_serial"] = uniqueSerial
                }
            }
        };

        await service.ProcessNodeUpdateAsync(newNodeId, payloadNovoAgente);

        // 3. Recarrega do banco
        var atualizada = await db.Maquinas
            .Include(m => m.Acoes)
            .FirstAsync(m => m.Id == maquina.Id);

        // Invariante J2: NUNCA substitui sozinho!
        Assert.Equal(oldNodeId, atualizada.NodeId); // Continua apontando para o antigo
        Assert.True(atualizada.EmAprovacao);
        Assert.Equal(newNodeId, atualizada.CandidatoNodeId);
        Assert.Equal("NEW-REFORMATTED-HOST", atualizada.CandidatoHostname);
        Assert.Equal("192.168.1.150", atualizada.CandidatoIp);
        Assert.NotNull(atualizada.CandidatoPrimeiroContatoEm);

        // Nenhuma ação enviada para o novo agente
        Assert.DoesNotContain(atualizada.Acoes, a => a.Estado == "enviada");
    }

    [Fact]
    public async Task ProcessNodeUpdate_SerieGenerica_IdentificaProvisorioComoSemSerie()
    {
        var (service, db, _) = CreateTestService();
        var genNodeId = "node//generic_" + Guid.NewGuid().ToString("N")[..6];

        var payload = new JsonObject
        {
            ["name"] = "CLONE-PC",
            ["conn"] = 1,
            ["hardware"] = new JsonObject
            {
                ["identifiers"] = new JsonObject
                {
                    ["bios_serial"] = "To be filled by O.E.M."
                }
            }
        };

        await service.ProcessNodeUpdateAsync(genNodeId, payload);

        var maquina = await db.Maquinas.FirstOrDefaultAsync(m => m.NodeId == genNodeId);
        Assert.NotNull(maquina);
        Assert.Equal($"SEM_SERIE_{genNodeId}", maquina.NumeroSerie);
        Assert.False(maquina.SerieManual);
    }

    [Fact]
    public async Task CorrigirSerieManual_AtualizaSerie_E_MarcaSerieManualTrue_NaoSobrescreveNoSync()
    {
        var (service, db, _) = CreateTestService();
        var genNodeId = "node//manual_" + Guid.NewGuid().ToString("N")[..6];

        // 1. Cria máquina sem série
        var payload = new JsonObject
        {
            ["name"] = "PC-SEM-SERIE",
            ["conn"] = 1,
            ["hardware"] = new JsonObject
            {
                ["identifiers"] = new JsonObject
                {
                    ["bios_serial"] = "System Serial Number"
                }
            }
        };
        await service.ProcessNodeUpdateAsync(genNodeId, payload);
        var maquina = await db.Maquinas.FirstAsync(m => m.NodeId == genNodeId);

        // 2. Técnico corrige manualmente a série (J2)
        var serieCorreta = "CORRETA_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var ok = await service.CorrigirSerieManualAsync(maquina.Id, serieCorreta, "tecnico_joao");
        Assert.True(ok);

        var maquinaCorrigida = await db.Maquinas.FirstAsync(m => m.Id == maquina.Id);
        Assert.Equal(serieCorreta, maquinaCorrigida.NumeroSerie);
        Assert.True(maquinaCorrigida.SerieManual);

        // 3. Nova sincronização do MeshCentral com a série genérica NÃO deve sobrescrever a série manual
        await service.ProcessNodeUpdateAsync(genNodeId, payload);
        var maquinaAposSync = await db.Maquinas.FirstAsync(m => m.Id == maquina.Id);
        Assert.Equal(serieCorreta, maquinaAposSync.NumeroSerie);
        Assert.True(maquinaAposSync.SerieManual);
    }

    [Fact]
    public async Task AprovarTrocaAgente_AmbosOnline_RecusaAprovacao()
    {
        var (service, db, _) = CreateTestService();
        var uniqueSerial = "BRG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var maquina = new Maquina
        {
            NumeroSerie = uniqueSerial,
            NodeId = "node//old_online",
            ConexaoEstado = "online", // Agente antigo online
            EmAprovacao = true,
            CandidatoNodeId = "node//new_online",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        // Invariante spec.md:37: Ambos online -> recusa aprovação
        var result = await service.AprovarTrocaAgenteAsync(
            maquina.Id,
            "tecnico_admin",
            manterBloqueio: false,
            justificativaLimpezaBloqueio: "Ok",
            tecnicoPodeBloquear: true);

        Assert.False(result.Sucesso);
        Assert.Contains("ambos os agentes online", result.MensagemErro);
    }

    [Fact]
    public async Task AprovarTrocaAgente_ComPermissaoBloquear_SemManterBloqueio_ExigeJustificativa()
    {
        var (service, db, _) = CreateTestService();
        var uniqueSerial = "BRG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var maquina = new Maquina
        {
            NumeroSerie = uniqueSerial,
            NodeId = "node//old_offline",
            ConexaoEstado = "offline",
            BloqueioEstado = "bloqueada", // Máquina estava bloqueada
            EmAprovacao = true,
            CandidatoNodeId = "node//new_agent",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        // Sem justificativa ao limpar bloqueio de máquina bloqueada
        var result = await service.AprovarTrocaAgenteAsync(
            maquina.Id,
            "tecnico_admin",
            manterBloqueio: false,
            justificativaLimpezaBloqueio: null, // Sem justificativa!
            tecnicoPodeBloquear: true);

        Assert.False(result.Sucesso);
        Assert.Contains("justificativa", result.MensagemErro, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AprovarTrocaAgente_ComJustificativa_Aprova_CancelaAcoesAntigas_AgendaGarantirResgate()
    {
        var (service, db, _) = CreateTestService();
        var uniqueSerial = "BRG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var maquina = new Maquina
        {
            NumeroSerie = uniqueSerial,
            NodeId = "node//old_offline",
            ConexaoEstado = "offline",
            BloqueioEstado = "bloqueada",
            EmAprovacao = true,
            CandidatoNodeId = "node//new_candidate",
            CandidatoHostname = "NEW-REFORMATTED",
            SenhaResgateCandidataCifrada = "candidata_antiga_cifrada",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        // Ações antigas na máquina
        var acaoBloqueioAntiga = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = "bloquear",
            Prioridade = 2,
            Estado = "pendente",
            Ator = "sistema"
        };
        var acaoAppAntiga = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = "instalar",
            Prioridade = 3,
            Estado = "pendente",
            Ator = "sistema"
        };
        db.Acoes.AddRange(acaoBloqueioAntiga, acaoAppAntiga);
        await db.SaveChangesAsync();

        var result = await service.AprovarTrocaAgenteAsync(
            maquina.Id,
            "tecnico_admin",
            manterBloqueio: false,
            justificativaLimpezaBloqueio: "Notebook reformatado e devolvido pelo usuário.",
            tecnicoPodeBloquear: true);

        Assert.True(result.Sucesso);

        var atualizada = await db.Maquinas
            .Include(m => m.Acoes)
            .FirstAsync(m => m.Id == maquina.Id);

        // 1. Aponta para o novo agente
        Assert.Equal("node//new_candidate", atualizada.NodeId);
        Assert.False(atualizada.EmAprovacao);
        Assert.Null(atualizada.CandidatoNodeId);
        Assert.Equal("desbloqueada", atualizada.BloqueioEstado);

        // 2. Ação de bloqueio antiga CANCELADA
        var bloqueioReloaded = atualizada.Acoes.First(a => a.Id == acaoBloqueioAntiga.Id);
        Assert.Equal("cancelada", bloqueioReloaded.Estado);

        // 3. Ação de instalação de app MANTIDA
        var appReloaded = atualizada.Acoes.First(a => a.Id == acaoAppAntiga.Id);
        Assert.Equal("pendente", appReloaded.Estado);

        // 4. Nova ação de garantir_resgate criada com prioridade 1
        var novoResgate = atualizada.Acoes.FirstOrDefault(a => a.Tipo == "garantir_resgate" && a.Estado == "pendente");
        Assert.NotNull(novoResgate);
        Assert.Equal(1, novoResgate.Prioridade);

        // 5. Auditorias gravadas
        var auditAprovacao = await db.Auditorias.FirstOrDefaultAsync(a => a.Tipo == TiposAuditoria.AprovarRejeitarMaquina && a.Alvo == uniqueSerial);
        Assert.NotNull(auditAprovacao);
        Assert.True(auditAprovacao.Sucesso);
    }

    [Fact]
    public async Task AprovarTrocaAgente_SemPermissaoBloquear_MantemBloqueioObrigatoriamente()
    {
        var (service, db, _) = CreateTestService();
        var uniqueSerial = "BRG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var maquina = new Maquina
        {
            NumeroSerie = uniqueSerial,
            NodeId = "node//old_offline_02",
            ConexaoEstado = "offline",
            BloqueioEstado = "bloqueada",
            EmAprovacao = true,
            CandidatoNodeId = "node//new_agent_02",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        // Técnico sem permissão 'bloquear' (tecnicoPodeBloquear = false)
        var result = await service.AprovarTrocaAgenteAsync(
            maquina.Id,
            "tecnico_sem_bloqueio",
            manterBloqueio: false, // Mesmo passando false, a regra J2 impõe manter o bloqueio!
            justificativaLimpezaBloqueio: null,
            tecnicoPodeBloquear: false);

        Assert.True(result.Sucesso);

        var atualizada = await db.Maquinas
            .Include(m => m.Acoes)
            .FirstAsync(m => m.Id == maquina.Id);

        // Estado do bloqueio foi mantido e gerou nova ação de bloqueio
        Assert.Equal("bloqueio_pendente", atualizada.BloqueioEstado);
        Assert.Contains(atualizada.Acoes, a => a.Tipo == "bloquear" && a.Estado == "pendente");
    }

    [Fact]
    public async Task RejeitarTrocaAgente_LimpaCandidato_RegistraAuditoria()
    {
        var (service, db, _) = CreateTestService();
        var uniqueSerial = "BRG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var maquina = new Maquina
        {
            NumeroSerie = uniqueSerial,
            NodeId = "node//old_agent_keep",
            ConexaoEstado = "offline",
            EmAprovacao = true,
            CandidatoNodeId = "node//bad_candidate",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        var ok = await service.RejeitarTrocaAgenteAsync(maquina.Id, "tecnico_ana");
        Assert.True(ok);

        var atualizada = await db.Maquinas.FirstAsync(m => m.Id == maquina.Id);
        Assert.False(atualizada.EmAprovacao);
        Assert.Null(atualizada.CandidatoNodeId);
        Assert.Equal("node//old_agent_keep", atualizada.NodeId); // Permanece com o antigo

        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.Tipo == TiposAuditoria.AprovarRejeitarMaquina && a.Alvo == uniqueSerial && !a.Sucesso);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task UnirRegistros_MigraHistorico_CancelaAcoesAntigas_AgendaGarantirResgate()
    {
        var (service, db, _) = CreateTestService();
        var serialDestino = "SERIE_DESTINO_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var serialOrigem = "SEM_SERIE_ORIGEM_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var destino = new Maquina
        {
            NumeroSerie = serialDestino,
            Hostname = "DEST-PC",
            ConexaoEstado = "offline",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        var origem = new Maquina
        {
            NumeroSerie = serialOrigem,
            NodeId = "node//origem_node",
            Hostname = "ORIGEM-PC",
            ConexaoEstado = "online",
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.AddRange(destino, origem);
        await db.SaveChangesAsync();

        // Adiciona histórico na origem
        db.HistoricoUsuarioMaquinas.Add(new HistoricoUsuarioMaquina
        {
            MaquinaId = origem.Id,
            Usuario = "roberto",
            InicioEm = DateTimeOffset.UtcNow
        });
        db.Acoes.Add(new Acao
        {
            MaquinaId = origem.Id,
            Tipo = "instalar",
            Prioridade = 3,
            Estado = "pendente",
            Ator = "sistema"
        });
        await db.SaveChangesAsync();

        var ok = await service.UnirRegistrosAsync(
            origem.Id,
            destino.Id,
            "tecnico_unir",
            manterBloqueio: false,
            justificativaLimpezaBloqueio: null,
            tecnicoPodeBloquear: true);

        Assert.True(ok);

        // Origem foi excluída
        var origemExiste = await db.Maquinas.AnyAsync(m => m.Id == origem.Id);
        Assert.False(origemExiste);

        // Destino recebeu nodeId, histórico e agendou resgate
        var destinoAtualizado = await db.Maquinas
            .Include(m => m.HistoricoUsuarios)
            .Include(m => m.Acoes)
            .FirstAsync(m => m.Id == destino.Id);

        Assert.Equal("node//origem_node", destinoAtualizado.NodeId);
        Assert.Contains(destinoAtualizado.HistoricoUsuarios, h => h.Usuario == "roberto");
        Assert.Contains(destinoAtualizado.Acoes, a => a.Tipo == "garantir_resgate" && a.Estado == "pendente");
    }

    [Fact]
    public async Task SyncHistoricoUsuarios_RegistraEntradaESaidaDeUsuarios()
    {
        var (service, db, _) = CreateTestService();
        var uniqueSerial = "BRG_HIST_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var nodeId = "node//hist_" + Guid.NewGuid().ToString("N")[..6];

        // 1. Primeiro evento: usuario 'alice' logado
        var p1 = new JsonObject
        {
            ["name"] = "PC-HIST",
            ["conn"] = 1,
            ["hardware"] = new JsonObject { ["identifiers"] = new JsonObject { ["bios_serial"] = uniqueSerial } },
            ["users"] = new JsonArray { "alice" }
        };
        await service.ProcessNodeUpdateAsync(nodeId, p1);

        var m1 = await db.Maquinas.Include(m => m.HistoricoUsuarios).FirstAsync(m => m.NumeroSerie == uniqueSerial);
        Assert.Single(m1.HistoricoUsuarios);
        Assert.Equal("alice", m1.HistoricoUsuarios.First().Usuario);
        Assert.Null(m1.HistoricoUsuarios.First().FimEm);

        // 2. Segundo evento: 'alice' e 'bob' logados
        var p2 = new JsonObject
        {
            ["name"] = "PC-HIST",
            ["conn"] = 1,
            ["hardware"] = new JsonObject { ["identifiers"] = new JsonObject { ["bios_serial"] = uniqueSerial } },
            ["users"] = new JsonArray { "alice", "bob" }
        };
        await service.ProcessNodeUpdateAsync(nodeId, p2);

        var m2 = await db.Maquinas.Include(m => m.HistoricoUsuarios).FirstAsync(m => m.NumeroSerie == uniqueSerial);
        Assert.Equal(2, m2.HistoricoUsuarios.Count);
        Assert.All(m2.HistoricoUsuarios, h => Assert.Null(h.FimEm));

        // 3. Terceiro evento: apenas 'bob' (alice fez logoff)
        var p3 = new JsonObject
        {
            ["name"] = "PC-HIST",
            ["conn"] = 1,
            ["hardware"] = new JsonObject { ["identifiers"] = new JsonObject { ["bios_serial"] = uniqueSerial } },
            ["users"] = new JsonArray { "bob" }
        };
        await service.ProcessNodeUpdateAsync(nodeId, p3);

        var m3 = await db.Maquinas.Include(m => m.HistoricoUsuarios).FirstAsync(m => m.NumeroSerie == uniqueSerial);
        var aliceHist = m3.HistoricoUsuarios.First(h => h.Usuario == "alice");
        var bobHist = m3.HistoricoUsuarios.First(h => h.Usuario == "bob");

        Assert.NotNull(aliceHist.FimEm); // Sessão de Alice foi encerrada!
        Assert.Null(bobHist.FimEm);       // Bob continua com sessão ativa
    }

    [Fact]
    public async Task DownloadInstalador_RegistraAuditoriaObrigatoria()
    {
        var (service, db, _) = CreateTestService();

        await service.RegistrarDownloadInstaladorAsync("tecnico_instalador", "10.0.0.5");

        var audit = await db.Auditorias.FirstOrDefaultAsync(a => a.Tipo == TiposAuditoria.BaixarInstalador && a.Ator == "tecnico_instalador");
        Assert.NotNull(audit);
        Assert.True(audit.Sucesso);
        Assert.Equal("10.0.0.5", audit.IpOrigem);
    }
}
