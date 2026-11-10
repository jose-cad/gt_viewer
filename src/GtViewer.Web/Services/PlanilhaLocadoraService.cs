using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GtViewer.Web.Services;

public class PlanilhaLocadoraService : IPlanilhaLocadoraService
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    private static readonly HashSet<string> NomesCabecalhoAceitos = new(StringComparer.OrdinalIgnoreCase)
    {
        "numero de serie",
        "número de série",
        "serial number",
        "serial",
        "série",
        "serie",
        "ns",
        "n/s",
        "nr serie",
        "nr. serie",
        "num serie",
        "num. serie",
        "nº de serie",
        "nº serie"
    };

    private readonly GtViewerDbContext _db;
    private readonly ILogger<PlanilhaLocadoraService> _logger;

    public PlanilhaLocadoraService(
        GtViewerDbContext db,
        ILogger<PlanilhaLocadoraService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PreviaPlanilhaResult> ProcessarPreviaAsync(
        Stream stream,
        string nomeArquivo,
        long tamanhoBytes,
        string ator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(nomeArquivo))
        {
            throw new ArgumentException("Nome do arquivo não pode ser vazio.", nameof(nomeArquivo));
        }

        var ext = Path.GetExtension(nomeArquivo).ToLowerInvariant();
        if (ext != ".xlsx")
        {
            throw new InvalidOperationException("Formato inválido. O arquivo deve ser uma planilha Excel (.xlsx).");
        }

        if (tamanhoBytes > MaxFileSizeBytes)
        {
            throw new InvalidOperationException("O arquivo excede o limite máximo permitido de 5 MB.");
        }

        XLWorkbook workbook;
        try
        {
            // Copia para memory stream caso o stream não suporte Seek
            if (!stream.CanSeek)
            {
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken);
                ms.Position = 0;
                workbook = new XLWorkbook(ms);
            }
            else
            {
                workbook = new XLWorkbook(stream);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlanilhaLocadora] Falha ao abrir arquivo Excel: {NomeArquivo}", nomeArquivo);
            throw new InvalidOperationException("Arquivo Excel corrompido ou ilegível.");
        }

        using (workbook)
        {
            var worksheet = workbook.Worksheets.FirstOrDefault();
            if (worksheet == null || worksheet.RowCount() == 0)
            {
                throw new InvalidOperationException("A planilha não possui abas ou está vazia.");
            }

            // 1. Procura o cabeçalho da coluna de série nas 10 primeiras linhas
            var (headerRow, serialCol) = LocalizarColunaSerie(worksheet);
            if (headerRow == 0 || serialCol == 0)
            {
                throw new InvalidOperationException(
                    "Coluna de número de série não encontrada nas 10 primeiras linhas. " +
                    "Nomes aceitos: Número de Série, Serial Number, Serial, Série, NS, N/S, Nr. Série, Num. Série.");
            }

            // 2. Leitura das linhas de dados a partir de headerRow + 1
            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;
            var seriaisValidos = new List<(string Serial, int Linha)>();
            var seriaisUnicos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var totalLinhas = 0;
            var seriesRepetidas = 0;
            var linhasIgnoradas = 0;

            for (var r = headerRow + 1; r <= lastRow; r++)
            {
                totalLinhas++;
                var cell = worksheet.Cell(r, serialCol);

                // Lê a série como texto preservando zeros à esquerda
                var rawVal = cell.GetFormattedString();
                if (string.IsNullOrWhiteSpace(rawVal))
                {
                    rawVal = cell.Value.ToString();
                }

                if (string.IsNullOrWhiteSpace(rawVal))
                {
                    linhasIgnoradas++;
                    continue;
                }

                // Normalização: maiúsculas, sem espaços
                var serialLimpo = Regex.Replace(rawVal.Trim(), @"\s+", "").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(serialLimpo))
                {
                    linhasIgnoradas++;
                    continue;
                }

                if (seriaisUnicos.Contains(serialLimpo))
                {
                    seriesRepetidas++;
                }
                else
                {
                    seriaisUnicos.Add(serialLimpo);
                    seriaisValidos.Add((serialLimpo, r));
                }
            }

            // 3. Validação de séries válidas
            if (seriaisValidos.Count == 0)
            {
                throw new InvalidOperationException("Nenhuma série válida encontrada na planilha. Confirmação bloqueada.");
            }

            // 4. Queda de mais de 20% em relação ao contrato vigente
            var contratoVigente = await _db.ContratosImportacao
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Vigente, cancellationToken);

            var exigeConfirmacaoReforcada = false;
            double quedaPercentual = 0;
            var seriesVigenteAnterior = contratoVigente?.SeriesValidas ?? 0;

            if (contratoVigente != null && contratoVigente.SeriesValidas > 0)
            {
                if (seriaisValidos.Count < contratoVigente.SeriesValidas)
                {
                    var diferenca = contratoVigente.SeriesValidas - seriaisValidos.Count;
                    var queda = (double)diferenca / contratoVigente.SeriesValidas;
                    if (queda > 0.20)
                    {
                        exigeConfirmacaoReforcada = true;
                        quedaPercentual = queda * 100.0;
                    }
                }
            }

            // 5. Grava a prévia no banco com auditoria na mesma transação
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var importacao = new ContratoImportacao
            {
                NomeArquivo = Path.GetFileName(nomeArquivo),
                TotalLinhas = totalLinhas,
                SeriesValidas = seriaisValidos.Count,
                SeriesRepetidas = seriesRepetidas,
                LinhasIgnoradas = linhasIgnoradas,
                Confirmado = false,
                Vigente = false,
                Ator = ator,
                CriadoEm = DateTimeOffset.UtcNow
            };

            _db.ContratosImportacao.Add(importacao);
            await _db.SaveChangesAsync(cancellationToken);

            foreach (var item in seriaisValidos)
            {
                var serialEntity = new ContratoSerial
                {
                    ContratoImportacaoId = importacao.Id,
                    NumeroSerie = item.Serial,
                    LinhaPlanilha = item.Linha
                };
                _db.ContratosSeriais.Add(serialEntity);
            }
            await _db.SaveChangesAsync(cancellationToken);

            var audit = new Auditoria
            {
                DataHora = DateTimeOffset.UtcNow,
                Ator = ator,
                Tipo = TiposAuditoria.ImportarPlanilha,
                Alvo = importacao.NomeArquivo,
                Sucesso = true,
                DetalhesJson = JsonSerializer.Serialize(new
                {
                    operacao = "previa",
                    importacaoId = importacao.Id,
                    totalLinhas,
                    seriesValidas = seriaisValidos.Count,
                    seriesRepetidas,
                    linhasIgnoradas,
                    exigeConfirmacaoReforcada,
                    quedaPercentual
                })
            };
            _db.Auditorias.Add(audit);
            await _db.SaveChangesAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
            _logger.LogInformation("[PlanilhaLocadora] Prévia de importação #{Id} gravada por {Ator}: {Validas} séries.",
                importacao.Id, ator, seriaisValidos.Count);

            return new PreviaPlanilhaResult(
                importacao.Id,
                importacao.NomeArquivo,
                totalLinhas,
                seriaisValidos.Count,
                seriesRepetidas,
                linhasIgnoradas,
                exigeConfirmacaoReforcada,
                quedaPercentual,
                seriesVigenteAnterior,
                seriaisValidos.Take(10).Select(s => s.Serial).ToList()
            );
        }
    }

    public async Task<ContratoImportacao> ConfirmarImportacaoAsync(
        int importacaoId,
        string ator,
        bool confirmacaoReforcadaAceita = false,
        CancellationToken cancellationToken = default)
    {
        var importacao = await _db.ContratosImportacao
            .Include(c => c.Seriais)
            .FirstOrDefaultAsync(c => c.Id == importacaoId, cancellationToken);

        if (importacao == null)
        {
            throw new KeyNotFoundException($"Importação com ID {importacaoId} não encontrada.");
        }

        if (importacao.Confirmado && importacao.Vigente)
        {
            return importacao; // Já confirmado e vigente
        }

        if (importacao.SeriesValidas == 0)
        {
            throw new InvalidOperationException("Não é possível confirmar uma importação sem séries válidas.");
        }

        // Verifica se a importação exigia confirmação reforçada
        var contratoVigente = await _db.ContratosImportacao
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Vigente && c.Id != importacaoId, cancellationToken);

        if (contratoVigente != null && contratoVigente.SeriesValidas > 0)
        {
            var diferenca = contratoVigente.SeriesValidas - importacao.SeriesValidas;
            if (diferenca > 0)
            {
                var queda = (double)diferenca / contratoVigente.SeriesValidas;
                if (queda > 0.20 && !confirmacaoReforcadaAceita)
                {
                    throw new InvalidOperationException(
                        $"A quantidade de séries caiu {queda * 100:F1}% em relação ao contrato vigente. " +
                        "É necessária confirmação reforçada para aplicar este contrato.");
                }
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Desmarca contratos vigentes anteriores
        var vigentes = await _db.ContratosImportacao
            .Where(c => c.Vigente)
            .ToListAsync(cancellationToken);

        foreach (var v in vigentes)
        {
            v.Vigente = false;
        }

        importacao.Confirmado = true;
        importacao.Vigente = true;
        importacao.ConfirmadoEm = DateTimeOffset.UtcNow;

        var audit = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = ator,
            Tipo = TiposAuditoria.ImportarPlanilha,
            Alvo = importacao.NomeArquivo,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new
            {
                operacao = "confirmar",
                importacaoId = importacao.Id,
                importacao.SeriesValidas,
                vigente = true
            })
        };
        _db.Auditorias.Add(audit);
        await _db.SaveChangesAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        _logger.LogInformation("[PlanilhaLocadora] Importação #{Id} confirmada como vigente por {Ator}.", importacao.Id, ator);
        return importacao;
    }

    public async Task<ContratoImportacao?> ObterContratoVigenteAsync(CancellationToken cancellationToken = default)
    {
        return await _db.ContratosImportacao
            .Include(c => c.Seriais)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Vigente, cancellationToken);
    }

    public async Task<List<ContratoImportacao>> ListarHistoricoImportacoesAsync(CancellationToken cancellationToken = default)
    {
        return await _db.ContratosImportacao
            .AsNoTracking()
            .OrderByDescending(c => c.CriadoEm)
            .ToListAsync(cancellationToken);
    }

    public async Task<RelatorioConferenciaResult> ObterRelatorioConferenciaAsync(CancellationToken cancellationToken = default)
    {
        var contratoVigente = await _db.ContratosImportacao
            .Include(c => c.Seriais)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Vigente, cancellationToken);

        if (contratoVigente == null)
        {
            return new RelatorioConferenciaResult(
                null,
                new List<MaquinaConferenciaItem>(),
                new List<MaquinaConferenciaItem>(),
                new List<MaquinaConferenciaItem>()
            );
        }

        var maquinas = await _db.Maquinas
            .AsNoTracking()
            .Where(m => !m.EmAprovacao)
            .ToListAsync(cancellationToken);

        var seriaisContrato = contratoVigente.Seriais
            .Select(s => s.NumeroSerie)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var maquinasPorSerie = maquinas
            .GroupBy(m => m.NumeroSerie, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var noContratoENoInventario = new List<MaquinaConferenciaItem>();
        var noContratoENuncaApareceuOuSumida = new List<MaquinaConferenciaItem>();
        var noInventarioEForaDoContrato = new List<MaquinaConferenciaItem>();

        // 1 e 2: Percorre todas as séries do contrato vigente
        foreach (var serial in seriaisContrato)
        {
            if (maquinasPorSerie.TryGetValue(serial, out var maq))
            {
                var item = new MaquinaConferenciaItem(
                    maq.NumeroSerie,
                    maq.Hostname,
                    maq.Modelo,
                    maq.Fabricante,
                    maq.ConexaoEstado,
                    maq.UltimoContatoEm
                );

                if (maq.ConexaoEstado == "sumida")
                {
                    noContratoENuncaApareceuOuSumida.Add(item);
                }
                else
                {
                    noContratoENoInventario.Add(item);
                }
            }
            else
            {
                // No contrato e nunca apareceu
                noContratoENuncaApareceuOuSumida.Add(new MaquinaConferenciaItem(
                    serial,
                    null,
                    null,
                    null,
                    "nunca apareceu",
                    null
                ));
            }
        }

        // 3: Percorre o inventário para achar as fora do contrato
        foreach (var maq in maquinas)
        {
            if (!seriaisContrato.Contains(maq.NumeroSerie))
            {
                noInventarioEForaDoContrato.Add(new MaquinaConferenciaItem(
                    maq.NumeroSerie,
                    maq.Hostname,
                    maq.Modelo,
                    maq.Fabricante,
                    maq.ConexaoEstado,
                    maq.UltimoContatoEm
                ));
            }
        }

        return new RelatorioConferenciaResult(
            contratoVigente,
            noContratoENoInventario.OrderBy(m => m.NumeroSerie).ToList(),
            noContratoENuncaApareceuOuSumida.OrderBy(m => m.NumeroSerie).ToList(),
            noInventarioEForaDoContrato.OrderBy(m => m.NumeroSerie).ToList()
        );
    }

    private static (int HeaderRow, int SerialCol) LocalizarColunaSerie(IXLWorksheet worksheet)
    {
        var maxRows = Math.Min(10, worksheet.RowCount());
        for (var r = 1; r <= maxRows; r++)
        {
            var row = worksheet.Row(r);
            if (row.IsEmpty()) continue;

            var lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (var c = 1; c <= lastCol; c++)
            {
                var cell = row.Cell(c);
                var text = cell.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var clean = Regex.Replace(text, @"\s+", " ");
                if (NomesCabecalhoAceitos.Contains(clean))
                {
                    return (r, c);
                }
            }
        }

        return (0, 0);
    }
}
