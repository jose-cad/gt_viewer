using ClosedXML.Excel;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GtViewer.Web.Services;

public class AuditoriaFiltro
{
    public string? Ator { get; set; }
    public string? Maquina { get; set; }
    public string? Tipo { get; set; }
    public DateTimeOffset? DataInicio { get; set; }
    public DateTimeOffset? DataFim { get; set; }
    public bool? Sucesso { get; set; }
    public string? EstadoAcao { get; set; }

    public int Pagina { get; set; } = 1;
    public int TamanhoPagina { get; set; } = 50;
}

public class AuditoriaItemViewModel
{
    public long Id { get; set; }
    public DateTimeOffset DataHora { get; set; }
    public string Ator { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string? Alvo { get; set; }
    public int? MaquinaId { get; set; }
    public string? MaquinaHostname { get; set; }
    public string? MaquinaNumeroSerie { get; set; }
    public int? AcaoId { get; set; }
    public string? AcaoTipo { get; set; }
    public string? AcaoEstado { get; set; }
    public string? AcaoResultado { get; set; }
    public bool Sucesso { get; set; }
    public string? DetalhesJson { get; set; }
    public string? IpOrigem { get; set; }
}

public class AuditoriaPagedResult
{
    public List<AuditoriaItemViewModel> Itens { get; set; } = new();
    public int TotalItens { get; set; }
    public int PaginaAtual { get; set; }
    public int TamanhoPagina { get; set; }
    public int TotalPaginas => (int)Math.Ceiling((double)TotalItens / Math.Max(1, TamanhoPagina));
}

public interface IAuditoriaService
{
    Task<Auditoria> RegistrarAsync(
        string ator,
        string tipo,
        string? alvo = null,
        bool sucesso = true,
        string? detalhesJson = null,
        string? ipOrigem = null,
        int? maquinaId = null,
        int? acaoId = null,
        CancellationToken cancellationToken = default);

    Task<AuditoriaPagedResult> ConsultarAsync(
        AuditoriaFiltro filtro,
        CancellationToken cancellationToken = default);

    Task<byte[]> ExportarExcelAsync(
        AuditoriaFiltro filtro,
        CancellationToken cancellationToken = default);

    Task<List<string>> ObterAtoresDistintosAsync(
        CancellationToken cancellationToken = default);

    Task ReconhecerAlertaEmergenciaAsync(
        int alertaId,
        string ator,
        CancellationToken cancellationToken = default);

    Task AlterarGrupoAsync(
        int grupoId,
        string nome,
        string? descricao,
        string ator,
        CancellationToken cancellationToken = default);
}

public class AuditoriaService : IAuditoriaService
{
    private readonly GtViewerDbContext _dbContext;

    public AuditoriaService(GtViewerDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Auditoria> RegistrarAsync(
        string ator,
        string tipo,
        string? alvo = null,
        bool sucesso = true,
        string? detalhesJson = null,
        string? ipOrigem = null,
        int? maquinaId = null,
        int? acaoId = null,
        CancellationToken cancellationToken = default)
    {
        var audit = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = string.IsNullOrWhiteSpace(ator) ? "sistema" : ator,
            Tipo = tipo,
            Alvo = alvo,
            Sucesso = sucesso,
            DetalhesJson = detalhesJson,
            IpOrigem = ipOrigem,
            MaquinaId = maquinaId,
            AcaoId = acaoId
        };

        _dbContext.Auditorias.Add(audit);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return audit;
    }

    public async Task<AuditoriaPagedResult> ConsultarAsync(
        AuditoriaFiltro filtro,
        CancellationToken cancellationToken = default)
    {
        var query = ConstruirQueryFiltrada(filtro);

        var totalItens = await query.CountAsync(cancellationToken);

        var pagina = Math.Max(1, filtro.Pagina);
        var tamanho = Math.Clamp(filtro.TamanhoPagina, 10, 500);

        var itensDb = await query
            .OrderByDescending(a => a.DataHora)
            .Skip((pagina - 1) * tamanho)
            .Take(tamanho)
            .ToListAsync(cancellationToken);

        var maquinasIds = itensDb.Where(a => a.MaquinaId.HasValue).Select(a => a.MaquinaId!.Value).Distinct().ToList();
        var acoesIds = itensDb.Where(a => a.AcaoId.HasValue).Select(a => a.AcaoId!.Value).Distinct().ToList();

        var maquinasDict = await _dbContext.Maquinas
            .AsNoTracking()
            .Where(m => maquinasIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var acoesDict = await _dbContext.Acoes
            .AsNoTracking()
            .Where(a => acoesIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var itens = itensDb.Select(a =>
        {
            maquinasDict.TryGetValue(a.MaquinaId ?? -1, out var maq);
            acoesDict.TryGetValue(a.AcaoId ?? -1, out var acao);

            return new AuditoriaItemViewModel
            {
                Id = a.Id,
                DataHora = a.DataHora,
                Ator = a.Ator,
                Tipo = a.Tipo,
                Alvo = a.Alvo,
                MaquinaId = a.MaquinaId,
                MaquinaHostname = maq?.Hostname,
                MaquinaNumeroSerie = maq?.NumeroSerie,
                AcaoId = a.AcaoId,
                AcaoTipo = acao?.Tipo,
                AcaoEstado = acao?.Estado,
                AcaoResultado = acao?.Resultado,
                Sucesso = a.Sucesso,
                DetalhesJson = a.DetalhesJson,
                IpOrigem = a.IpOrigem
            };
        }).ToList();

        return new AuditoriaPagedResult
        {
            Itens = itens,
            TotalItens = totalItens,
            PaginaAtual = pagina,
            TamanhoPagina = tamanho
        };
    }

    public async Task<byte[]> ExportarExcelAsync(
        AuditoriaFiltro filtro,
        CancellationToken cancellationToken = default)
    {
        // Limita exportação a até 5000 registros para garantir estabilidade
        filtro.Pagina = 1;
        filtro.TamanhoPagina = 5000;
        var paged = await ConsultarAsync(filtro, cancellationToken);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Auditoria");

        // Cabeçalhos
        var headers = new[]
        {
            "ID", "Data/Hora (UTC)", "Ator", "Tipo", "Alvo",
            "Máquina Hostname", "Máquina Série", "ID Ação", "Estado Ação",
            "Sucesso", "IP Origem", "Detalhes"
        };

        for (var c = 0; c < headers.Length; c++)
        {
            var cell = worksheet.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(240, 240, 240);
        }

        var row = 2;
        foreach (var item in paged.Itens)
        {
            worksheet.Cell(row, 1).SetValue(item.Id);
            worksheet.Cell(row, 2).SetValue(item.DataHora.ToString("yyyy-MM-dd HH:mm:ss"));
            worksheet.Cell(row, 3).SetValue(item.Ator);
            worksheet.Cell(row, 4).SetValue(item.Tipo);
            worksheet.Cell(row, 5).SetValue(item.Alvo ?? string.Empty);
            worksheet.Cell(row, 6).SetValue(item.MaquinaHostname ?? string.Empty);
            worksheet.Cell(row, 7).SetValue(item.MaquinaNumeroSerie ?? string.Empty);
            worksheet.Cell(row, 8).SetValue(item.AcaoId.HasValue ? item.AcaoId.Value.ToString() : string.Empty);
            worksheet.Cell(row, 9).SetValue(item.AcaoEstado ?? string.Empty);
            worksheet.Cell(row, 10).SetValue(item.Sucesso ? "SIM" : "NÃO");
            worksheet.Cell(row, 11).SetValue(item.IpOrigem ?? string.Empty);
            worksheet.Cell(row, 12).SetValue(item.DetalhesJson ?? string.Empty);
            row++;
        }

        worksheet.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<List<string>> ObterAtoresDistintosAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Auditorias
            .AsNoTracking()
            .Select(a => a.Ator)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(cancellationToken);
    }

    public async Task ReconhecerAlertaEmergenciaAsync(
        int alertaId,
        string ator,
        CancellationToken cancellationToken = default)
    {
        var alerta = await _dbContext.AlertasEmergencia.FindAsync([alertaId], cancellationToken);
        if (alerta == null)
        {
            throw new InvalidOperationException($"Alerta de emergência #{alertaId} não encontrado.");
        }

        if (alerta.Reconhecido) return;

        alerta.Reconhecido = true;
        alerta.ReconhecidoEm = DateTimeOffset.UtcNow;
        alerta.ReconhecidoPor = ator;

        var audit = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = ator,
            Tipo = TiposAuditoria.ReconhecerAlerta,
            Alvo = $"Alerta #{alertaId}",
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new
            {
                alertaId = alerta.Id,
                mensagem = alerta.Mensagem,
                geradoPor = alerta.Ator
            })
        };

        _dbContext.Auditorias.Add(audit);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AlterarGrupoAsync(
        int grupoId,
        string nome,
        string? descricao,
        string ator,
        CancellationToken cancellationToken = default)
    {
        var grupo = await _dbContext.Grupos.FindAsync([grupoId], cancellationToken);
        if (grupo == null)
        {
            throw new InvalidOperationException($"Grupo #{grupoId} não encontrado.");
        }

        var nomeAnterior = grupo.Nome;
        grupo.Nome = nome;
        grupo.Descricao = descricao;
        grupo.AtualizadoEm = DateTimeOffset.UtcNow;

        var audit = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = ator,
            Tipo = TiposAuditoria.AlterarGrupo,
            Alvo = grupo.Nome,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new
            {
                grupoId = grupo.Id,
                nomeAnterior,
                nomeNovo = nome,
                descricao
            })
        };

        _dbContext.Auditorias.Add(audit);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<Auditoria> ConstruirQueryFiltrada(AuditoriaFiltro filtro)
    {
        var query = _dbContext.Auditorias.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filtro.Ator))
        {
            var atorTrim = filtro.Ator.Trim().ToLower();
            query = query.Where(a => a.Ator.ToLower().Contains(atorTrim));
        }

        if (!string.IsNullOrWhiteSpace(filtro.Tipo))
        {
            var tipoTrim = filtro.Tipo.Trim();
            query = query.Where(a => a.Tipo == tipoTrim);
        }

        if (filtro.DataInicio.HasValue)
        {
            query = query.Where(a => a.DataHora >= filtro.DataInicio.Value);
        }

        if (filtro.DataFim.HasValue)
        {
            query = query.Where(a => a.DataHora <= filtro.DataFim.Value);
        }

        if (filtro.Sucesso.HasValue)
        {
            query = query.Where(a => a.Sucesso == filtro.Sucesso.Value);
        }

        if (!string.IsNullOrWhiteSpace(filtro.Maquina))
        {
            var maqTrim = filtro.Maquina.Trim().ToLower();
            var idsMaquinas = _dbContext.Maquinas
                .Where(m => (m.Hostname != null && m.Hostname.ToLower().Contains(maqTrim))
                         || (m.NumeroSerie != null && m.NumeroSerie.ToLower().Contains(maqTrim)))
                .Select(m => m.Id);

            query = query.Where(a =>
                (a.Alvo != null && a.Alvo.ToLower().Contains(maqTrim)) ||
                (a.MaquinaId.HasValue && idsMaquinas.Contains(a.MaquinaId.Value)));
        }

        if (!string.IsNullOrWhiteSpace(filtro.EstadoAcao))
        {
            var estTrim = filtro.EstadoAcao.Trim();
            var idsAcoes = _dbContext.Acoes
                .Where(ac => ac.Estado == estTrim)
                .Select(ac => ac.Id);

            query = query.Where(a => a.AcaoId.HasValue && idsAcoes.Contains(a.AcaoId.Value));
        }

        return query;
    }
}
