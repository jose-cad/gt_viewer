using System.Text.Json;
using System.Text.Json.Nodes;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.Inventario;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GtViewer.Web.Services;

public class InventarioService : IInventarioService
{
    private readonly GtViewerDbContext _dbContext;
    private readonly IAuditoriaService _auditoriaService;
    private readonly IRescuePasswordService _rescuePasswordService;
    private readonly IFilaAcoesService? _filaAcoesService;
    private readonly ILogger<InventarioService> _logger;

    private static readonly HashSet<string> GenericSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "To be filled by O.E.M.",
        "To be filled by OEM",
        "Default string",
        "System Serial Number",
        "0123456789",
        "1234567890",
        "None",
        "N/A",
        "System manufacturer",
        "Chassis Serial Number",
        "Base Board Serial Number",
        "Not Applicable",
        "Unknown"
    };

    public InventarioService(
        GtViewerDbContext dbContext,
        IAuditoriaService auditoriaService,
        IRescuePasswordService rescuePasswordService,
        ILogger<InventarioService> logger,
        IFilaAcoesService? filaAcoesService = null)
    {
        _dbContext = dbContext;
        _auditoriaService = auditoriaService;
        _rescuePasswordService = rescuePasswordService;
        _logger = logger;
        _filaAcoesService = filaAcoesService;
    }

    public static bool IsSerialGenerico(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return true;
        var clean = serial.Trim();
        return GenericSerials.Contains(clean) || clean.Length < 3;
    }

    public async Task ProcessNodeUpdateAsync(string nodeId, JsonNode? nodeData, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeData == null) return;

        // Extrai dados brutos do MeshCentral
        var rawSerial = nodeData["hardware"]?["identifiers"]?["bios_serial"]?.ToString() ??
                        nodeData["bios_serial"]?.ToString();

        var isGenerico = IsSerialGenerico(rawSerial);
        var normalizedSerial = isGenerico ? $"SEM_SERIE_{nodeId}" : rawSerial!.Trim().ToUpperInvariant();

        var model = nodeData["hardware"]?["identifiers"]?["product_name"]?.ToString() ??
                    nodeData["name"]?.ToString();

        var manufacturer = nodeData["hardware"]?["identifiers"]?["chassis_manufacturer"]?.ToString() ??
                           nodeData["hardware"]?["identifiers"]?["board_vendor"]?.ToString();

        var osDesc = nodeData["osdesc"]?.ToString();
        var arch = nodeData["hardware"]?["windows"]?["osinfo"]?["OSArchitecture"]?.ToString() ??
                   (osDesc?.Contains("64") == true ? "64-bit" : null);

        var hostname = nodeData["name"]?.ToString();
        var ip = nodeData["ip"]?.ToString();

        var conn = (nodeData["conn"]?.GetValue<int>() ?? 0) & 1;
        var isOnline = conn != 0;

        // Antivírus
        string? avName = null;
        bool? avActive = null;
        bool? avUpdated = null;

        var avArray = nodeData["av"]?.AsArray();
        if (avArray != null && avArray.Count > 0)
        {
            var firstAv = avArray[0];
            avName = firstAv?["product"]?.ToString();
            avActive = firstAv?["enabled"]?.GetValue<bool>() ?? false;
            avUpdated = firstAv?["updated"]?.GetValue<bool>() ?? false;
        }

        // Usuários interativos logados
        var currentUsers = new List<string>();
        var usersArr = nodeData["users"]?.AsArray();
        if (usersArr != null)
        {
            foreach (var u in usersArr)
            {
                var val = u?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) currentUsers.Add(val.Trim());
            }
        }

        // 1. Localiza por NodeId existente
        var maquina = await _dbContext.Maquinas
            .Include(m => m.HistoricoUsuarios)
            .FirstOrDefaultAsync(m => m.NodeId == nodeId, cancellationToken);

        if (maquina != null)
        {
            // Atualiza campos de telemetria
            if (!maquina.SerieManual && !isGenerico && !maquina.NumeroSerie.StartsWith("SEM_SERIE_"))
            {
                maquina.NumeroSerie = normalizedSerial;
            }

            ApplyTelemetry(maquina, model, manufacturer, osDesc, arch, hostname, isOnline, avName, avActive, avUpdated, currentUsers);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // 2. Não existe pelo NodeId: procura por Número de Série
        maquina = await _dbContext.Maquinas
            .Include(m => m.HistoricoUsuarios)
            .FirstOrDefaultAsync(m => m.NumeroSerie == normalizedSerial, cancellationToken);

        if (maquina != null)
        {
            // Conflito de série já conhecida (reinstalação ou duplicata)
            // Invariante J2: NUNCA substitui sozinho. Fica em "Aguardando aprovação".
            maquina.EmAprovacao = true;
            maquina.CandidatoNodeId = nodeId;
            maquina.CandidatoHostname = hostname;
            maquina.CandidatoIp = ip;
            maquina.CandidatoPrimeiroContatoEm ??= DateTimeOffset.UtcNow;
            maquina.CandidatoUltimoContatoEm = DateTimeOffset.UtcNow;
            maquina.AtualizadoEm = DateTimeOffset.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("[Inventario] Máquina com série conhecida {Serie} conectou com novo agente {NodeId}. Aguardando aprovação.",
                normalizedSerial, nodeId);
            return;
        }

        // 3. Série nova: cadastra nova máquina
        var novaMaquina = new Maquina
        {
            NumeroSerie = normalizedSerial,
            SerieManual = false,
            NodeId = nodeId,
            Hostname = hostname,
            Modelo = model,
            Fabricante = manufacturer,
            SistemaOperacional = osDesc,
            Arquitetura = arch,
            AntivirusNome = avName,
            AntivirusAtivo = avActive,
            AntivirusAtualizado = avUpdated,
            ConexaoEstado = isOnline ? "online" : "offline",
            BloqueioEstado = "desbloqueada",
            PrimeiroContatoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow,
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };

        // Agenda garantia da conta de resgate com senha única inicial se não for SEM_SERIE
        if (!normalizedSerial.StartsWith("SEM_SERIE_"))
        {
            var plainRescuePassword = _rescuePasswordService.GeneratePassword();
            novaMaquina.SenhaResgateCandidataCifrada = _rescuePasswordService.EncryptPassword(plainRescuePassword);
        }

        _dbContext.Maquinas.Add(novaMaquina);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Cria ação de fila e auditoria para garantir a conta de resgate
        if (!normalizedSerial.StartsWith("SEM_SERIE_"))
        {
            if (_filaAcoesService != null)
            {
                await _filaAcoesService.EnfileirarAcaoAsync(
                    novaMaquina.Id,
                    "garantir_resgate",
                    "sistema",
                    "{\"motivo\":\"Cadastro inicial de máquina\"}",
                    cancellationToken: cancellationToken);
            }
            else
            {
                var acao = new Acao
                {
                    MaquinaId = novaMaquina.Id,
                    Tipo = "garantir_resgate",
                    Prioridade = 1,
                    Estado = "pendente",
                    Ator = "sistema",
                    ParametrosJson = "{\"motivo\":\"Cadastro inicial de máquina\"}",
                    CriadoEm = DateTimeOffset.UtcNow
                };
                _dbContext.Acoes.Add(acao);
                await _dbContext.SaveChangesAsync(cancellationToken);

                await _auditoriaService.RegistrarAsync(
                    ator: "sistema",
                    tipo: TiposAuditoria.GarantirContaResgate,
                    alvo: novaMaquina.NumeroSerie,
                    maquinaId: novaMaquina.Id,
                    acaoId: acao.Id,
                    detalhesJson: "{\"acao\":\"garantir_conta_resgate_inicial\"}",
                    cancellationToken: cancellationToken);
            }
        }

        // Histórico de usuários
        SyncHistoricoUsuarios(novaMaquina, currentUsers);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ApplyTelemetry(
        Maquina maquina,
        string? model,
        string? manufacturer,
        string? osDesc,
        string? arch,
        string? hostname,
        bool isOnline,
        string? avName,
        bool? avActive,
        bool? avUpdated,
        List<string> currentUsers)
    {
        maquina.Modelo = model ?? maquina.Modelo;
        maquina.Fabricante = manufacturer ?? maquina.Fabricante;
        maquina.SistemaOperacional = osDesc ?? maquina.SistemaOperacional;
        maquina.Arquitetura = arch ?? maquina.Arquitetura;
        maquina.Hostname = hostname ?? maquina.Hostname;
        maquina.AntivirusNome = avName ?? maquina.AntivirusNome;
        maquina.AntivirusAtivo = avActive ?? maquina.AntivirusAtivo;
        maquina.AntivirusAtualizado = avUpdated ?? maquina.AntivirusAtualizado;

        if (isOnline)
        {
            maquina.ConexaoEstado = "online";
            maquina.UltimoContatoEm = DateTimeOffset.UtcNow;
        }
        else
        {
            maquina.ConexaoEstado = maquina.UltimoContatoEm < DateTimeOffset.UtcNow.AddDays(-30) ? "sumida" : "offline";
        }

        maquina.UsuariosLogadosJson = JsonSerializer.Serialize(currentUsers);
        maquina.AtualizadoEm = DateTimeOffset.UtcNow;

        SyncHistoricoUsuarios(maquina, currentUsers);
    }

    private void SyncHistoricoUsuarios(Maquina maquina, List<string> currentUsers)
    {
        var now = DateTimeOffset.UtcNow;
        var activeRecords = maquina.HistoricoUsuarios.Where(h => h.FimEm == null).ToList();

        // Usuários novos
        foreach (var user in currentUsers)
        {
            if (!activeRecords.Any(r => string.Equals(r.Usuario, user, StringComparison.OrdinalIgnoreCase)))
            {
                maquina.HistoricoUsuarios.Add(new HistoricoUsuarioMaquina
                {
                    MaquinaId = maquina.Id,
                    Usuario = user,
                    InicioEm = now,
                    FimEm = null,
                    CriadoEm = now
                });
            }
        }

        // Usuários que saíram
        foreach (var record in activeRecords)
        {
            if (!currentUsers.Any(u => string.Equals(u, record.Usuario, StringComparison.OrdinalIgnoreCase)))
            {
                record.FimEm = now;
            }
        }
    }

    public async Task FullSyncAsync(IReadOnlyDictionary<string, JsonNode> nodes, CancellationToken cancellationToken = default)
    {
        foreach (var (nodeId, nodeData) in nodes)
        {
            try
            {
                await ProcessNodeUpdateAsync(nodeId, nodeData, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Inventario] Erro ao sincronizar nó {NodeId} no FullSync.", nodeId);
            }
        }

        // Atualiza estado de máquinas que não estão no MeshCentral para offline ou sumida
        var onlineCutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var offlineMaquinas = await _dbContext.Maquinas
            .Where(m => m.ConexaoEstado == "online" && !nodes.ContainsKey(m.NodeId ?? ""))
            .ToListAsync(cancellationToken);

        foreach (var m in offlineMaquinas)
        {
            m.ConexaoEstado = m.UltimoContatoEm < onlineCutoff ? "sumida" : "offline";
            m.AtualizadoEm = DateTimeOffset.UtcNow;
        }

        if (offlineMaquinas.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MaquinaListItemDto>> ListMaquinasAsync(MaquinaFilterDto filter, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Maquinas
            .Include(m => m.Grupo)
            .AsNoTracking();

        if (filter.ApenasEmAprovacao)
        {
            query = query.Where(m => m.EmAprovacao);
        }

        if (filter.ApenasSemSerie)
        {
            query = query.Where(m => m.NumeroSerie.StartsWith("SEM_SERIE_"));
        }

        if (!string.IsNullOrWhiteSpace(filter.ConexaoEstado))
        {
            query = query.Where(m => m.ConexaoEstado == filter.ConexaoEstado);
        }

        if (!string.IsNullOrWhiteSpace(filter.BloqueioEstado))
        {
            query = query.Where(m => m.BloqueioEstado == filter.BloqueioEstado);
        }

        if (filter.GrupoId.HasValue)
        {
            query = query.Where(m => m.GrupoId == filter.GrupoId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Busca))
        {
            var termo = filter.Busca.Trim().ToLower();
            query = query.Where(m =>
                m.NumeroSerie.ToLower().Contains(termo) ||
                (m.Hostname != null && m.Hostname.ToLower().Contains(termo)) ||
                (m.UsuariosLogadosJson != null && m.UsuariosLogadosJson.ToLower().Contains(termo)));
        }

        var list = await query
            .OrderByDescending(m => m.EmAprovacao)
            .ThenByDescending(m => m.UltimoContatoEm)
            .ToListAsync(cancellationToken);

        // Consulta contrato vigente
        var contratoVigente = await _dbContext.ContratosImportacao
            .Include(c => c.Seriais)
            .FirstOrDefaultAsync(c => c.Vigente, cancellationToken);

        var seriesContrato = contratoVigente?.Seriais.Select(s => s.NumeroSerie).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<MaquinaListItemDto>();
        foreach (var m in list)
        {
            string situacaoContrato;
            if (contratoVigente == null)
            {
                situacaoContrato = "sem_planilha";
            }
            else
            {
                situacaoContrato = seriesContrato != null && seriesContrato.Contains(m.NumeroSerie)
                    ? "no_contrato"
                    : "fora_do_contrato";
            }

            if (!string.IsNullOrWhiteSpace(filter.SituacaoContrato) &&
                !string.Equals(situacaoContrato, filter.SituacaoContrato, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var avStatus = "Desconhecido";
            if (m.AntivirusAtivo == true && m.AntivirusAtualizado == true)
            {
                avStatus = "Ativo e Atualizado";
            }
            else if (m.AntivirusAtivo == false || m.AntivirusAtualizado == false)
            {
                avStatus = "Com Problema";
            }

            var usersText = "";
            if (!string.IsNullOrEmpty(m.UsuariosLogadosJson))
            {
                try
                {
                    var uList = JsonSerializer.Deserialize<List<string>>(m.UsuariosLogadosJson);
                    if (uList != null) usersText = string.Join(", ", uList);
                }
                catch { }
            }

            result.Add(new MaquinaListItemDto
            {
                Id = m.Id,
                NumeroSerie = m.NumeroSerie,
                SerieManual = m.SerieManual,
                NodeId = m.NodeId,
                Hostname = m.Hostname,
                Modelo = m.Modelo,
                Fabricante = m.Fabricante,
                SistemaOperacional = m.SistemaOperacional,
                UsuariosLogados = usersText,
                ConexaoEstado = m.ConexaoEstado,
                BloqueioEstado = m.BloqueioEstado,
                SituacaoContrato = situacaoContrato,
                AntivirusStatus = avStatus,
                UltimoContatoEm = m.UltimoContatoEm,
                EmAprovacao = m.EmAprovacao,
                GrupoNome = m.Grupo?.Nome
            });
        }

        return result;
    }

    public async Task<MaquinaDetailDto?> GetMaquinaDetailAsync(int id, CancellationToken cancellationToken = default)
    {
        var m = await _dbContext.Maquinas
            .Include(x => x.Grupo)
            .Include(x => x.HistoricoUsuarios)
            .Include(x => x.Acoes)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (m == null) return null;

        var contratoVigente = await _dbContext.ContratosImportacao
            .Include(c => c.Seriais)
            .FirstOrDefaultAsync(c => c.Vigente, cancellationToken);

        string situacaoContrato;
        if (contratoVigente == null)
        {
            situacaoContrato = "sem_planilha";
        }
        else
        {
            situacaoContrato = contratoVigente.Seriais.Any(s => string.Equals(s.NumeroSerie, m.NumeroSerie, StringComparison.OrdinalIgnoreCase))
                ? "no_contrato"
                : "fora_do_contrato";
        }

        var avStatus = "Desconhecido";
        if (m.AntivirusAtivo == true && m.AntivirusAtualizado == true)
        {
            avStatus = "Ativo e Atualizado";
        }
        else if (m.AntivirusAtivo == false || m.AntivirusAtualizado == false)
        {
            avStatus = "Com Problema";
        }

        var ultimasAuditorias = await _dbContext.Auditorias
            .Where(a => a.MaquinaId == m.Id || a.Alvo == m.NumeroSerie)
            .OrderByDescending(a => a.DataHora)
            .Take(10)
            .Select(a => new AuditoriaResumoDto(a.Id, a.DataHora, a.Ator, a.Tipo, a.Sucesso, a.Alvo))
            .ToListAsync(cancellationToken);

        return new MaquinaDetailDto
        {
            Id = m.Id,
            NumeroSerie = m.NumeroSerie,
            SerieManual = m.SerieManual,
            NodeId = m.NodeId,
            Hostname = m.Hostname,
            Modelo = m.Modelo,
            Fabricante = m.Fabricante,
            SistemaOperacional = m.SistemaOperacional,
            Arquitetura = m.Arquitetura,
            AntivirusNome = m.AntivirusNome,
            AntivirusAtivo = m.AntivirusAtivo,
            AntivirusAtualizado = m.AntivirusAtualizado,
            AntivirusStatus = avStatus,
            UsuariosLogadosJson = m.UsuariosLogadosJson,
            ConexaoEstado = m.ConexaoEstado,
            BloqueioEstado = m.BloqueioEstado,
            MensagemBloqueio = m.MensagemBloqueio,
            SituacaoContrato = situacaoContrato,
            PrimeiroContatoEm = m.PrimeiroContatoEm,
            UltimoContatoEm = m.UltimoContatoEm,
            EmAprovacao = m.EmAprovacao,
            CandidatoNodeId = m.CandidatoNodeId,
            CandidatoHostname = m.CandidatoHostname,
            CandidatoIp = m.CandidatoIp,
            CandidatoPrimeiroContatoEm = m.CandidatoPrimeiroContatoEm,
            CandidatoUltimoContatoEm = m.CandidatoUltimoContatoEm,
            GrupoId = m.GrupoId,
            GrupoNome = m.Grupo?.Nome,
            HistoricoUsuarios = m.HistoricoUsuarios
                .OrderByDescending(h => h.InicioEm)
                .Select(h => new HistoricoUsuarioDto(h.Id, h.Usuario, h.InicioEm, h.FimEm))
                .ToList(),
            AcoesPendentes = m.Acoes
                .Where(a => a.Estado == "pendente" || a.Estado == "enviada" || a.Estado == "resultado_desconhecido")
                .OrderBy(a => a.Prioridade)
                .ThenBy(a => a.CriadoEm)
                .Select(a => new AcaoResumoDto(a.Id, a.Tipo, a.Prioridade, a.Estado, a.Ator, a.CriadoEm))
                .ToList(),
            UltimasAuditorias = ultimasAuditorias
        };
    }

    public async Task<AprovarMaquinaResult> AprovarTrocaAgenteAsync(
        int maquinaId,
        string tecnicoUsername,
        bool manterBloqueio,
        string? justificativaLimpezaBloqueio,
        bool tecnicoPodeBloquear,
        CancellationToken cancellationToken = default)
    {
        var maquina = await _dbContext.Maquinas
            .Include(m => m.Acoes)
            .FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);

        if (maquina == null || !maquina.EmAprovacao || string.IsNullOrEmpty(maquina.CandidatoNodeId))
        {
            return new AprovarMaquinaResult(false, "Máquina não encontrada ou não está aguardando aprovação.");
        }

        // Invariante spec.md:37: Ambos online ao mesmo tempo -> não aprova troca
        if (maquina.ConexaoEstado == "online")
        {
            return new AprovarMaquinaResult(false, "Não é possível aprovar a troca com ambos os agentes online ao mesmo tempo. Aguarde o desligamento do anterior ou resolva o conflito.");
        }

        var estavaBloqueada = maquina.BloqueioEstado == "bloqueada" || maquina.BloqueioEstado == "bloqueio_pendente";

        // Regra de bloqueio
        if (tecnicoPodeBloquear)
        {
            if (!manterBloqueio && estavaBloqueada)
            {
                if (string.IsNullOrWhiteSpace(justificativaLimpezaBloqueio))
                {
                    return new AprovarMaquinaResult(false, "É obrigatório fornecer justificativa para limpar o bloqueio de uma máquina bloqueada.");
                }
                maquina.BloqueioEstado = "desbloqueada";
            }
        }
        else
        {
            // Técnico sem permissão 'bloquear': o estado de bloqueio é mantido obrigatoriamente
            manterBloqueio = estavaBloqueada;
        }

        // Cancela ações antigas de bloqueio, desbloqueio e senha de resgate do agente anterior
        var tiposParaCancelar = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bloquear", "desbloquear", "garantir_resgate", "trocar_resgate", "redefinir_resgate"
        };

        foreach (var acao in maquina.Acoes.Where(a => tiposParaCancelar.Contains(a.Tipo) && (a.Estado == "pendente" || a.Estado == "resultado_desconhecido")))
        {
            acao.Estado = "cancelada";
            acao.Detalhes = "Cancelada pela aprovação de novo agente na máquina.";
        }

        // Descarta candidata antiga de senha de resgate
        maquina.SenhaResgateCandidataCifrada = null;

        // Atualiza para o novo agente
        maquina.NodeId = maquina.CandidatoNodeId;
        maquina.Hostname = maquina.CandidatoHostname ?? maquina.Hostname;
        maquina.EmAprovacao = false;
        maquina.CandidatoNodeId = null;
        maquina.CandidatoHostname = null;
        maquina.CandidatoIp = null;
        maquina.CandidatoPrimeiroContatoEm = null;
        maquina.CandidatoUltimoContatoEm = null;
        maquina.AtualizadoEm = DateTimeOffset.UtcNow;

        // Agenda 'garantir conta de resgate' com prioridade máxima (1) e senha nova
        var novaSenhaResgate = _rescuePasswordService.GeneratePassword();
        maquina.SenhaResgateCandidataCifrada = _rescuePasswordService.EncryptPassword(novaSenhaResgate);

        var acaoResgate = new Acao
        {
            MaquinaId = maquina.Id,
            Tipo = "garantir_resgate",
            Prioridade = 1,
            Estado = "pendente",
            Ator = tecnicoUsername,
            ParametrosJson = "{\"motivo\":\"Aprovação de novo agente para a máquina\"}",
            CriadoEm = DateTimeOffset.UtcNow
        };
        _dbContext.Acoes.Add(acaoResgate);

        if (manterBloqueio)
        {
            maquina.BloqueioEstado = "bloqueio_pendente";
            var acaoBloqueio = new Acao
            {
                MaquinaId = maquina.Id,
                Tipo = "bloquear",
                Prioridade = 2,
                Estado = "pendente",
                Ator = tecnicoUsername,
                ParametrosJson = JsonSerializer.Serialize(new { manterBloqueio = true }),
                CriadoEm = DateTimeOffset.UtcNow
            };
            _dbContext.Acoes.Add(acaoBloqueio);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Auditoria
        await _auditoriaService.RegistrarAsync(
            ator: tecnicoUsername,
            tipo: TiposAuditoria.AprovarRejeitarMaquina,
            alvo: maquina.NumeroSerie,
            sucesso: true,
            maquinaId: maquina.Id,
            detalhesJson: JsonSerializer.Serialize(new
            {
                decisao = "aprovada",
                manterBloqueio,
                justificativa = justificativaLimpezaBloqueio
            }),
            cancellationToken: cancellationToken);

        await _auditoriaService.RegistrarAsync(
            ator: tecnicoUsername,
            tipo: TiposAuditoria.GarantirContaResgate,
            alvo: maquina.NumeroSerie,
            sucesso: true,
            maquinaId: maquina.Id,
            acaoId: acaoResgate.Id,
            detalhesJson: "{\"motivo\":\"Garantir resgate pós-aprovação de troca de agente\"}",
            cancellationToken: cancellationToken);

        return new AprovarMaquinaResult(true);
    }

    public async Task<bool> RejeitarTrocaAgenteAsync(int maquinaId, string tecnicoUsername, CancellationToken cancellationToken = default)
    {
        var maquina = await _dbContext.Maquinas.FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);
        if (maquina == null || !maquina.EmAprovacao) return false;

        var candidatoNode = maquina.CandidatoNodeId;

        maquina.EmAprovacao = false;
        maquina.CandidatoNodeId = null;
        maquina.CandidatoHostname = null;
        maquina.CandidatoIp = null;
        maquina.CandidatoPrimeiroContatoEm = null;
        maquina.CandidatoUltimoContatoEm = null;
        maquina.AtualizadoEm = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditoriaService.RegistrarAsync(
            ator: tecnicoUsername,
            tipo: TiposAuditoria.AprovarRejeitarMaquina,
            alvo: maquina.NumeroSerie,
            sucesso: false,
            maquinaId: maquina.Id,
            detalhesJson: JsonSerializer.Serialize(new
            {
                decisao = "rejeitada",
                candidatoNodeId = candidatoNode
            }),
            cancellationToken: cancellationToken);

        return true;
    }

    public async Task<bool> CorrigirSerieManualAsync(int maquinaId, string novaSerie, string tecnicoUsername, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(novaSerie)) return false;

        var maquina = await _dbContext.Maquinas.FirstOrDefaultAsync(m => m.Id == maquinaId, cancellationToken);
        if (maquina == null) return false;

        var serieLimpa = novaSerie.Trim().ToUpperInvariant();
        var serieAnterior = maquina.NumeroSerie;

        maquina.NumeroSerie = serieLimpa;
        maquina.SerieManual = true; // Invariante J2: marcada como manual para não ser sobrescrita pelo bios_serial
        maquina.AtualizadoEm = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditoriaService.RegistrarAsync(
            ator: tecnicoUsername,
            tipo: TiposAuditoria.CorrigirSerie,
            alvo: serieLimpa,
            sucesso: true,
            maquinaId: maquina.Id,
            detalhesJson: JsonSerializer.Serialize(new
            {
                serieAnterior,
                novaSerie = serieLimpa
            }),
            cancellationToken: cancellationToken);

        return true;
    }

    public async Task<bool> UnirRegistrosAsync(
        int maquinaOrigemId,
        int maquinaDestinoId,
        string tecnicoUsername,
        bool manterBloqueio,
        string? justificativaLimpezaBloqueio,
        bool tecnicoPodeBloquear,
        CancellationToken cancellationToken = default)
    {
        if (maquinaOrigemId == maquinaDestinoId) return false;

        var origem = await _dbContext.Maquinas
            .Include(m => m.HistoricoUsuarios)
            .Include(m => m.Acoes)
            .FirstOrDefaultAsync(m => m.Id == maquinaOrigemId, cancellationToken);

        var destino = await _dbContext.Maquinas
            .Include(m => m.HistoricoUsuarios)
            .Include(m => m.Acoes)
            .FirstOrDefaultAsync(m => m.Id == maquinaDestinoId, cancellationToken);

        if (origem == null || destino == null) return false;

        // Migra histórico de usuários
        foreach (var h in origem.HistoricoUsuarios)
        {
            h.MaquinaId = destino.Id;
        }

        // Cancela ações antigas da origem
        foreach (var a in origem.Acoes.Where(x => x.Estado == "pendente" || x.Estado == "resultado_desconhecido"))
        {
            a.Estado = "cancelada";
            a.Detalhes = $"Cancelada por união com a máquina #{destino.Id}.";
        }

        // Se a origem tiver NodeId ativo e o destino não tiver, adota
        if (string.IsNullOrEmpty(destino.NodeId) && !string.IsNullOrEmpty(origem.NodeId))
        {
            destino.NodeId = origem.NodeId;
            destino.Hostname = origem.Hostname;
        }

        // Agenda garantir conta de resgate no destino
        var novaSenha = _rescuePasswordService.GeneratePassword();
        destino.SenhaResgateCandidataCifrada = _rescuePasswordService.EncryptPassword(novaSenha);

        var acaoResgate = new Acao
        {
            MaquinaId = destino.Id,
            Tipo = "garantir_resgate",
            Prioridade = 1,
            Estado = "pendente",
            Ator = tecnicoUsername,
            ParametrosJson = "{\"motivo\":\"União de registros de máquinas\"}",
            CriadoEm = DateTimeOffset.UtcNow
        };
        _dbContext.Acoes.Add(acaoResgate);

        // Remove registro órfão de origem
        _dbContext.Maquinas.Remove(origem);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditoriaService.RegistrarAsync(
            ator: tecnicoUsername,
            tipo: TiposAuditoria.UnirRegistros,
            alvo: destino.NumeroSerie,
            sucesso: true,
            maquinaId: destino.Id,
            detalhesJson: JsonSerializer.Serialize(new
            {
                origemId = maquinaOrigemId,
                destinoId = maquinaDestinoId,
                serieOrigem = origem.NumeroSerie,
                serieDestino = destino.NumeroSerie
            }),
            cancellationToken: cancellationToken);

        return true;
    }

    public async Task RegistrarDownloadInstaladorAsync(string tecnicoUsername, string? ipOrigem, CancellationToken cancellationToken = default)
    {
        await _auditoriaService.RegistrarAsync(
            ator: tecnicoUsername,
            tipo: TiposAuditoria.BaixarInstalador,
            alvo: "MeshAgent.exe",
            sucesso: true,
            ipOrigem: ipOrigem,
            detalhesJson: "{\"arquivo\":\"MeshAgent.exe\",\"versao\":\"1.3.1\"}",
            cancellationToken: cancellationToken);
    }
}
