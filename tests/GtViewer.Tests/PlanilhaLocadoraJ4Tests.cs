using System.Net;
using System.Text;
using ClosedXML.Excel;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Security;
using GtViewer.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GtViewer.Tests;

public class PlanilhaLocadoraJ4Tests : IClassFixture<WebApplicationFactory<GtViewer.Web.Program>>
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private readonly WebApplicationFactory<GtViewer.Web.Program> _factory;

    public PlanilhaLocadoraJ4Tests(WebApplicationFactory<GtViewer.Web.Program> factory)
    {
        _factory = factory;
    }

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private static (PlanilhaLocadoraService service, GtViewerDbContext db) CreateTestServices()
    {
        var db = new GtViewerDbContext(CreateOptions());
        var service = new PlanilhaLocadoraService(db, NullLogger<PlanilhaLocadoraService>.Instance);
        return (service, db);
    }

    private static byte[] CreateWorkbookBytes(Action<IXLWorksheet> configureSheet)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Equipamentos");
        configureSheet(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task ProcessarPrevia_PlanilhaValida_CalculaEstatisticas_ENormalizaSeries()
    {
        var (service, db) = CreateTestServices();

        var bytes = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Número de Série";
            ws.Cell(1, 2).Value = "Modelo";
            ws.Cell(2, 1).Value = " sn-abc-001 ";
            ws.Cell(2, 2).Value = "Latitude 3420";
            ws.Cell(3, 1).Value = "SN-ABC-002";
            ws.Cell(3, 2).Value = "ThinkPad E14";
            ws.Cell(4, 1).Value = ""; // Linha vazia/ignorada
            ws.Cell(5, 1).Value = "SN-ABC-001"; // Repetida
        });

        using var stream = new MemoryStream(bytes);
        var previa = await service.ProcessarPreviaAsync(stream, "contrato_locadora_nov2026.xlsx", bytes.Length, "admin.ti");

        Assert.Equal(4, previa.TotalLinhas);
        Assert.Equal(2, previa.SeriesValidas); // ABC-001 e ABC-002
        Assert.Equal(1, previa.SeriesRepetidas);
        Assert.Equal(1, previa.LinhasIgnoradas);
        Assert.Contains("SN-ABC-001", previa.SeriesAmostra);
        Assert.Contains("SN-ABC-002", previa.SeriesAmostra);

        // Verifica gravação no banco
        var impDb = await db.ContratosImportacao
            .Include(c => c.Seriais)
            .FirstOrDefaultAsync(c => c.Id == previa.ImportacaoId);

        Assert.NotNull(impDb);
        Assert.False(impDb.Confirmado);
        Assert.False(impDb.Vigente);
        Assert.Equal(2, impDb.Seriais.Count);

        // Verifica auditoria
        var audit = await db.Auditorias
            .OrderByDescending(a => a.DataHora)
            .FirstOrDefaultAsync(a => a.Tipo == TiposAuditoria.ImportarPlanilha && a.Alvo == impDb.NomeArquivo);

        Assert.NotNull(audit);
        Assert.True(audit.Sucesso);
        Assert.Contains("previa", audit.DetalhesJson!);
    }

    [Fact]
    public async Task ProcessarPrevia_CabecalhoNaLinha3_LocalizaCorretamente()
    {
        var (service, _) = CreateTestServices();

        var bytes = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "RELATÓRIO DE LOCAÇÃO - NOVEMBRO 2026";
            ws.Cell(2, 1).Value = ""; // em branco
            ws.Cell(3, 1).Value = "Item";
            ws.Cell(3, 2).Value = "Serial Number";
            ws.Cell(4, 1).Value = 1;
            ws.Cell(4, 2).Value = "DELL001";
            ws.Cell(5, 1).Value = 2;
            ws.Cell(5, 2).Value = "DELL002";
        });

        using var stream = new MemoryStream(bytes);
        var previa = await service.ProcessarPreviaAsync(stream, "locacao_linha3.xlsx", bytes.Length, "admin.ti");

        Assert.Equal(2, previa.SeriesValidas);
        Assert.Contains("DELL001", previa.SeriesAmostra);
        Assert.Contains("DELL002", previa.SeriesAmostra);
    }

    [Fact]
    public async Task ProcessarPrevia_SeriesNumericasComZerosAEsquerda_PreservaZeros()
    {
        var (service, _) = CreateTestServices();

        var bytes = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Serial";
            // Força formato texto ou número com zeros à esquerda
            ws.Cell(2, 1).SetValue("00012345");
            ws.Cell(3, 1).SetValue("00987654");
        });

        using var stream = new MemoryStream(bytes);
        var previa = await service.ProcessarPreviaAsync(stream, "zeros_esquerda.xlsx", bytes.Length, "admin.ti");

        Assert.Equal(2, previa.SeriesValidas);
        Assert.Contains("00012345", previa.SeriesAmostra);
        Assert.Contains("00987654", previa.SeriesAmostra);
        Assert.DoesNotContain("12345", previa.SeriesAmostra);
    }

    [Fact]
    public async Task ProcessarPrevia_SemColunaDeSerie_LancaExcecao()
    {
        var (service, _) = CreateTestServices();

        var bytes = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Nome";
            ws.Cell(1, 2).Value = "Departamento";
            ws.Cell(2, 1).Value = "Notebook TI";
            ws.Cell(2, 2).Value = "Suporte";
        });

        using var stream = new MemoryStream(bytes);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessarPreviaAsync(stream, "sem_serie.xlsx", bytes.Length, "admin.ti"));

        Assert.Contains("Coluna de número de série não encontrada", ex.Message);
        Assert.Contains("Número de Série", ex.Message);
    }

    [Fact]
    public async Task ProcessarPrevia_ZeroSeriesValidas_LancaExcecao()
    {
        var (service, _) = CreateTestServices();

        var bytes = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Número de Série";
            ws.Cell(2, 1).Value = "   "; // whitespace
            ws.Cell(3, 1).Value = "";
        });

        using var stream = new MemoryStream(bytes);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessarPreviaAsync(stream, "vazia.xlsx", bytes.Length, "admin.ti"));

        Assert.Contains("Nenhuma série válida", ex.Message);
    }

    [Fact]
    public async Task ProcessarPrevia_ArquivoCorrompido_LancaExcecao()
    {
        var (service, _) = CreateTestServices();

        var garbageBytes = Encoding.UTF8.GetBytes("isto não é um arquivo zip/xlsx válido");
        using var stream = new MemoryStream(garbageBytes);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessarPreviaAsync(stream, "corrompido.xlsx", garbageBytes.Length, "admin.ti"));

        Assert.Contains("corrompido ou ilegível", ex.Message);
    }

    [Fact]
    public async Task ProcessarPrevia_ArquivoAcimaDe5Mb_LancaExcecao()
    {
        var (service, _) = CreateTestServices();

        using var stream = new MemoryStream([1, 2, 3]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessarPreviaAsync(stream, "grande.xlsx", 6 * 1024 * 1024, "admin.ti"));

        Assert.Contains("5 MB", ex.Message);
    }

    [Fact]
    public async Task ProcessarPrevia_ExtensaoInvalida_LancaExcecao()
    {
        var (service, _) = CreateTestServices();

        using var stream = new MemoryStream([1, 2, 3]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessarPreviaAsync(stream, "planilha.csv", 100, "admin.ti"));

        Assert.Contains("(.xlsx)", ex.Message);
    }

    [Fact]
    public async Task ProcessarPrevia_ValoresComFormulasOuHtml_TrataComoTexto()
    {
        var (service, _) = CreateTestServices();

        var bytes = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Série";
            ws.Cell(2, 1).SetValue("<script>alert('xss')</script>");
            ws.Cell(3, 1).SetValue("=1+1");
        });

        using var stream = new MemoryStream(bytes);
        var previa = await service.ProcessarPreviaAsync(stream, "formulas.xlsx", bytes.Length, "admin.ti");

        Assert.Equal(2, previa.SeriesValidas);
        // O valor é preservado como texto sem interpretação/execução
        Assert.Contains(previa.SeriesAmostra, s => s.Contains("SCRIPT") || s.Contains("ALERT"));
    }

    [Fact]
    public async Task QuedaMaiorQue20PorCento_ExigeConfirmacaoReforcada()
    {
        var (service, db) = CreateTestServices();

        // 1. Cadastra contrato vigente com 10 séries
        var bytes10 = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Número de Série";
            for (var i = 1; i <= 10; i++)
            {
                ws.Cell(i + 1, 1).Value = $"SN-CONTRATO-{i:D3}";
            }
        });

        using var stream1 = new MemoryStream(bytes10);
        var previa1 = await service.ProcessarPreviaAsync(stream1, "contrato_10.xlsx", bytes10.Length, "admin.ti");
        await service.ConfirmarImportacaoAsync(previa1.ImportacaoId, "admin.ti");

        // 2. Envia nova planilha com 7 séries (queda de 30% > 20%)
        var bytes7 = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Número de Série";
            for (var i = 1; i <= 7; i++)
            {
                ws.Cell(i + 1, 1).Value = $"SN-CONTRATO-{i:D3}";
            }
        });

        using var stream2 = new MemoryStream(bytes7);
        var previa2 = await service.ProcessarPreviaAsync(stream2, "contrato_7.xlsx", bytes7.Length, "admin.ti");

        Assert.True(previa2.ExigeConfirmacaoReforcada);
        Assert.Equal(30.0, previa2.QuedaPercentual);
        Assert.Equal(10, previa2.SeriesVigenteAnterior);

        // Confirmação sem confirmação reforçada é recusada
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmarImportacaoAsync(previa2.ImportacaoId, "admin.ti", confirmacaoReforcadaAceita: false));

        Assert.Contains("confirmação reforçada", ex.Message);

        // Confirmação com confirmação reforçada aceita é aprovada
        var confirmado = await service.ConfirmarImportacaoAsync(previa2.ImportacaoId, "admin.ti", confirmacaoReforcadaAceita: true);
        Assert.True(confirmado.Vigente);
        Assert.Equal(7, confirmado.SeriesValidas);

        // O contrato anterior agora não é mais vigente
        var anterior = await db.ContratosImportacao.FindAsync(previa1.ImportacaoId);
        Assert.False(anterior!.Vigente);
    }

    [Fact]
    public async Task RelatorioConferencia_AsTresListasBatemComInventarioDeTeste()
    {
        var (service, db) = CreateTestServices();

        // 1. Cria máquinas de teste no banco
        var snNoContratoOnline = "SN-CONF-ONLINE-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var snNoContratoSumida = "SN-CONF-SUMIDA-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var snForaDoContrato = "SN-CONF-FORA-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var snNuncaApareceu = "SN-CONF-NUNCA-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        db.Maquinas.AddRange(
            new Maquina
            {
                NumeroSerie = snNoContratoOnline,
                Hostname = "NB-ONLINE",
                ConexaoEstado = "online",
                CriadoEm = DateTimeOffset.UtcNow,
                UltimoContatoEm = DateTimeOffset.UtcNow
            },
            new Maquina
            {
                NumeroSerie = snNoContratoSumida,
                Hostname = "NB-SUMIDA",
                ConexaoEstado = "sumida",
                CriadoEm = DateTimeOffset.UtcNow,
                UltimoContatoEm = DateTimeOffset.UtcNow.AddDays(-35)
            },
            new Maquina
            {
                NumeroSerie = snForaDoContrato,
                Hostname = "NB-FORA-CONTRATO",
                ConexaoEstado = "online",
                CriadoEm = DateTimeOffset.UtcNow,
                UltimoContatoEm = DateTimeOffset.UtcNow
            }
        );
        await db.SaveChangesAsync();

        // 2. Importa planilha com snNoContratoOnline, snNoContratoSumida e snNuncaApareceu
        var bytes = CreateWorkbookBytes(ws =>
        {
            ws.Cell(1, 1).Value = "Número de Série";
            ws.Cell(2, 1).Value = snNoContratoOnline;
            ws.Cell(3, 1).Value = snNoContratoSumida;
            ws.Cell(4, 1).Value = snNuncaApareceu;
        });

        using var stream = new MemoryStream(bytes);
        var previa = await service.ProcessarPreviaAsync(stream, "conferencia_3listas.xlsx", bytes.Length, "admin.ti");
        await service.ConfirmarImportacaoAsync(previa.ImportacaoId, "admin.ti", confirmacaoReforcadaAceita: true);

        // 3. Executa relatório de conferência
        var relatorio = await service.ObterRelatorioConferenciaAsync();

        Assert.NotNull(relatorio.ContratoVigente);

        // Lista 1: No contrato e no inventário (não sumida)
        Assert.Contains(relatorio.NoContratoENoInventario, m => m.NumeroSerie == snNoContratoOnline);
        Assert.DoesNotContain(relatorio.NoContratoENoInventario, m => m.NumeroSerie == snNoContratoSumida);

        // Lista 2: No contrato e nunca apareceu OU sumida
        Assert.Contains(relatorio.NoContratoENuncaApareceuOuSumida, m => m.NumeroSerie == snNoContratoSumida);
        Assert.Contains(relatorio.NoContratoENuncaApareceuOuSumida, m => m.NumeroSerie == snNuncaApareceu);

        // Lista 3: No inventário e fora do contrato
        Assert.Contains(relatorio.NoInventarioEForaDoContrato, m => m.NumeroSerie == snForaDoContrato);
        Assert.DoesNotContain(relatorio.NoInventarioEForaDoContrato, m => m.NumeroSerie == snNoContratoOnline);
    }

    [Fact]
    public async Task WebUi_Permissoes_ImportarPlanilha_Enforced()
    {
        var clientFactory = _factory.WithWebHostBuilder(_ => { });

        // 1. Acesso anônimo a /Contrato/Importar -> 302 Redireciona para /Login
        var anonClient = clientFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonResponse = await anonClient.GetAsync("/Contrato/Importar");
        Assert.Equal(HttpStatusCode.Redirect, anonResponse.StatusCode);
        Assert.Contains("/login", anonResponse.Headers.Location?.ToString().ToLower());
    }
}
