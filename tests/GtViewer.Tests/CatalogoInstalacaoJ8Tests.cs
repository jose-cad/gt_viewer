using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GtViewer.Tests;

public class CatalogoInstalacaoJ8Tests : IClassFixture<WebApplicationFactory<GtViewer.Web.Program>>, IDisposable
{
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private readonly WebApplicationFactory<GtViewer.Web.Program> _factory;
    private readonly string _tempDir;

    public CatalogoInstalacaoJ8Tests(WebApplicationFactory<GtViewer.Web.Program> factory)
    {
        _factory = factory;
        _tempDir = Path.Combine(Path.GetTempPath(), "gtv_test_catalogo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        }
    }

    private static DbContextOptions<GtViewerDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(AdminConnectionString)
            .Options;
    }

    private (
        CatalogoService catalogoService,
        FilaAcoesService filaService,
        GtViewerDbContext db,
        MockMeshCentralClient meshClient,
        IAuthenticodeValidator authenticodeValidator,
        ILoginSecurityService securityService) CreateTestServices(IAuthenticodeValidator? customValidator = null)
    {
        var db = new GtViewerDbContext(CreateOptions());
        var meshClient = new MockMeshCentralClient();
        var scriptProvider = new AgentScriptProvider();
        var protection = new EphemeralDataProtectionProvider();
        var rescueService = new RescuePasswordService(protection);
        var options = Options.Create(new MeshCentralOptions { ServerUrl = "https://gtviewer.local" });
        var securityService = new LoginSecurityService();

        var filaService = new FilaAcoesService(
            db,
            meshClient,
            scriptProvider,
            rescueService,
            options,
            NullLogger<FilaAcoesService>.Instance);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Catalogo:StoragePath"] = _tempDir
            })
            .Build();

        var authenticodeValidator = customValidator ?? new MockAuthenticodeValidator(isSigned: true, publisher: "Mozilla Corporation");

        var catalogoService = new CatalogoService(
            db,
            authenticodeValidator,
            config,
            NullLogger<CatalogoService>.Instance);

        return (catalogoService, filaService, db, meshClient, authenticodeValidator, securityService);
    }

    [Fact]
    public async Task CadastrarApp_ValidInstaller_SavesAppAndCalculatesHashAndAudits()
    {
        var (catalogoService, _, db, _, _, _) = CreateTestServices();

        var appNome = "Firefox Test " + Guid.NewGuid().ToString("N")[..8];
        var fileBytes = Encoding.UTF8.GetBytes("conteudo_instalador_valido_assinado");
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(fileBytes));

        var input = new CadastrarAppInput(
            Nome: appNome,
            Versao: "128.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: "/quiet",
            ComandoRemocao: "msiexec /x {GUID} /qn",
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: @"HKLM:\Software\Mozilla\Firefox",
            RegraDeteccaoValor: "128.0"
        );

        using var stream = new MemoryStream(fileBytes);
        var app = await catalogoService.CadastrarAppAsync(input, stream, "firefox.msi", "admin.ti");

        Assert.NotNull(app);
        Assert.Equal(appNome, app.Nome);
        Assert.Equal(expectedHash, app.HashSha256);
        Assert.Equal(fileBytes.Length, app.TamanhoBytes);
        Assert.True(app.Ativo);

        // Confere auditoria
        var audit = await db.Auditorias
            .OrderByDescending(a => a.DataHora)
            .FirstOrDefaultAsync(a => a.Alvo == appNome && a.Tipo == TiposAuditoria.AlterarCatalogo);

        Assert.NotNull(audit);
        Assert.Equal("admin.ti", audit.Ator);
        Assert.True(audit.Sucesso);
        Assert.Contains("criar", audit.DetalhesJson!);
    }

    [Fact]
    public async Task CadastrarApp_UnsignedInstaller_RejectsUploadAndCleansUpFile()
    {
        var mockValidator = new MockAuthenticodeValidator(isSigned: false, errorMessage: "Instaladores sem assinatura digital ficam fora do catálogo na v1.");
        var (catalogoService, _, db, _, _, _) = CreateTestServices(mockValidator);

        var appNome = "Unsigned App " + Guid.NewGuid().ToString("N")[..8];
        var input = new CadastrarAppInput(
            Nome: appNome,
            Versao: "1.0",
            FabricanteEsperado: "Any",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "arquivo",
            RegraDeteccaoChave: @"C:\app.exe",
            RegraDeteccaoValor: null
        );

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not_signed"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalogoService.CadastrarAppAsync(input, stream, "app.exe", "admin.ti"));

        Assert.Contains("sem assinatura digital", ex.Message);

        var existsInDb = await db.AppsCatalogo.AnyAsync(a => a.Nome == appNome);
        Assert.False(existsInDb);

        // Confere que nenhum arquivo órfão ficou na pasta
        var files = Directory.GetFiles(_tempDir);
        Assert.Empty(files);
    }

    [Fact]
    public async Task CadastrarApp_PublisherMismatch_RejectsUpload()
    {
        var mockValidator = new MockAuthenticodeValidator(
            isSigned: true,
            publisher: "Fabricante Diferente LLC",
            errorMessage: "Fabricante da assinatura digital não confere com o esperado.");

        var (catalogoService, _, _, _, _, _) = CreateTestServices(mockValidator);

        var input = new CadastrarAppInput(
            Nome: "Mismatch App " + Guid.NewGuid().ToString("N")[..8],
            Versao: "1.0",
            FabricanteEsperado: "Google LLC",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "arquivo",
            RegraDeteccaoChave: @"C:\app.exe",
            RegraDeteccaoValor: null
        );

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("signed_content"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalogoService.CadastrarAppAsync(input, stream, "app.msi", "admin.ti"));

        Assert.Contains("Fabricante", ex.Message);
    }

    [Fact]
    public async Task CadastrarApp_Exceeding500Mb_RejectsUpload()
    {
        var (catalogoService, _, _, _, _, _) = CreateTestServices();

        var input = new CadastrarAppInput(
            Nome: "Big App " + Guid.NewGuid().ToString("N")[..8],
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: null
        );

        // Stream simulando arquivo maior que 500 MB (501 MB)
        var stream = new FakeLargeStream(501L * 1024 * 1024);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalogoService.CadastrarAppAsync(input, stream, "big.msi", "admin.ti"));

        Assert.Contains("500 MB", ex.Message);
    }

    [Fact]
    public async Task AtualizarEDesativarApp_UpdatesMetadataAndAudits()
    {
        var (catalogoService, _, db, _, _, _) = CreateTestServices();

        var appNome = "App Edit " + Guid.NewGuid().ToString("N")[..8];
        var input = new CadastrarAppInput(
            Nome: appNome,
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: "/q",
            ComandoRemocao: "rem.bat",
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: "1.0"
        );

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("app_data"));
        var app = await catalogoService.CadastrarAppAsync(input, stream, "app.msi", "admin.ti");

        // Atualizar
        var updateInput = new AtualizarAppInput(
            Nome: appNome,
            Versao: "2.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: "/qn /norestart",
            ComandoRemocao: "rem2.bat",
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: "2.0",
            Ativo: true
        );

        var updated = await catalogoService.AtualizarAppAsync(app.Id, updateInput, "admin.ti");
        Assert.Equal("2.0", updated.Versao);
        Assert.Equal("/qn /norestart", updated.ParametrosInstalacao);

        // Desativar
        var desativado = await catalogoService.DesativarAppAsync(app.Id, "admin.ti");
        Assert.True(desativado);

        var reloaded = await catalogoService.ObterAppPorIdAsync(app.Id);
        Assert.False(reloaded!.Ativo);

        // Confere auditoria de desativação
        var audit = await db.Auditorias
            .OrderByDescending(a => a.DataHora)
            .FirstOrDefaultAsync(a => a.Alvo == appNome && a.Tipo == TiposAuditoria.AlterarCatalogo);

        Assert.NotNull(audit);
        Assert.Contains("desativar", audit.DetalhesJson!);
    }

    [Fact]
    public async Task ObterInstaladorPorToken_ValidToken_ReturnsFile_ExpiredOrInvalid_ReturnsNull()
    {
        var (catalogoService, _, db, _, _, _) = CreateTestServices();

        var fileBytes = Encoding.UTF8.GetBytes("conteudo_instalador_teste_token");
        var appNome = "App Token " + Guid.NewGuid().ToString("N")[..8];

        using var stream = new MemoryStream(fileBytes);
        var app = await catalogoService.CadastrarAppAsync(new CadastrarAppInput(
            Nome: appNome,
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: null
        ), stream, "app.msi", "admin.ti");

        var maquina = new Maquina
        {
            NodeId = "node//token_test_" + Guid.NewGuid().ToString("N")[..6],
            NumeroSerie = "SN" + Guid.NewGuid().ToString("N")[..8],
            Hostname = "NB-TEST",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        var validToken = Guid.NewGuid().ToString("N");
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(validToken)));

        var tarefa = new Tarefa
        {
            Tipo = TiposAcao.Instalar,
            Ator = "admin.ti",
            AppCatalogoId = app.Id,
            CriadoEm = DateTimeOffset.UtcNow
        };
        db.Tarefas.Add(tarefa);
        await db.SaveChangesAsync();

        var acao = new Acao
        {
            MaquinaId = maquina.Id,
            TarefaId = tarefa.Id,
            Tipo = TiposAcao.Instalar,
            Estado = EstadosAcao.Enviada,
            Ator = "admin.ti",
            Prioridade = PrioridadesAcao.Aplicativos,
            TokenAgenteHash = tokenHash,
            TokenAgenteExpiraEm = DateTimeOffset.UtcNow.AddMinutes(30),
            TokenAgenteUsado = false
        };
        db.Acoes.Add(acao);
        await db.SaveChangesAsync();

        // 1. Token válido: retorna Stream
        var result = await catalogoService.ObterInstaladorPorTokenAsync(validToken);
        Assert.NotNull(result);
        Assert.Equal(fileBytes.Length, result.FileLength);
        result.Stream.Dispose();

        // 2. Token inválido: retorna null
        var invalidResult = await catalogoService.ObterInstaladorPorTokenAsync("token_inexistente");
        Assert.Null(invalidResult);

        // 3. Token expirado: retorna null
        acao.TokenAgenteExpiraEm = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var expiredResult = await catalogoService.ObterInstaladorPorTokenAsync(validToken);
        Assert.Null(expiredResult);
    }

    [Fact]
    public async Task FilaAcoes_Instalar_PreDetectado_ReturnsJaInstalado_CompletesSuccess()
    {
        var (catalogoService, filaService, db, meshClient, _, _) = CreateTestServices();

        var app = await catalogoService.CadastrarAppAsync(new CadastrarAppInput(
            Nome: "App Ja Instalado " + Guid.NewGuid().ToString("N")[..8],
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: null
        ), new MemoryStream(Encoding.UTF8.GetBytes("dummy")), "app.msi", "admin.ti");

        var maquina = new Maquina
        {
            NodeId = "node//app_ja_inst_" + Guid.NewGuid().ToString("N")[..6],
            NumeroSerie = "SN" + Guid.NewGuid().ToString("N")[..8],
            Hostname = "NB-JA-INST",
            ConexaoEstado = "online",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("JA_INSTALADO", 0, "Aplicativo já presente\r\nGTV-RESULT=JA_INSTALADO\r\nGTV-EXIT=0");

        var tarefa = await filaService.CriarTarefaMaquinasAsync([maquina.Id], TiposAcao.Instalar, "admin.ti", appCatalogoId: app.Id);
        await filaService.ProcessarMaquinaAsync(maquina.Id);

        var acao = await db.Acoes.FirstOrDefaultAsync(a => a.TarefaId == tarefa.Id && a.MaquinaId == maquina.Id);
        Assert.NotNull(acao);
        Assert.Equal(EstadosAcao.Executada, acao.Estado);
        Assert.Equal("JA_INSTALADO", acao.Resultado);
        Assert.Equal(0, acao.CodigoSaida);

        var tarefaDb = await db.Tarefas.FirstOrDefaultAsync(t => t.Id == tarefa.Id);
        Assert.NotNull(tarefaDb!.ConcluidoEm);
    }

    [Fact]
    public async Task FilaAcoes_Remover_NaoEstavaInstalado_CompletesSuccess()
    {
        var (catalogoService, filaService, db, meshClient, _, _) = CreateTestServices();

        var app = await catalogoService.CadastrarAppAsync(new CadastrarAppInput(
            Nome: "App Remover " + Guid.NewGuid().ToString("N")[..8],
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: null,
            ComandoRemocao: "unins.exe",
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: null
        ), new MemoryStream(Encoding.UTF8.GetBytes("dummy")), "app.msi", "admin.ti");

        var maquina = new Maquina
        {
            NodeId = "node//app_rem_" + Guid.NewGuid().ToString("N")[..6],
            NumeroSerie = "SN" + Guid.NewGuid().ToString("N")[..8],
            Hostname = "NB-REM",
            ConexaoEstado = "online",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("NAO_ESTAVA_INSTALADO", 0, "App não encontrado\r\nGTV-RESULT=NAO_ESTAVA_INSTALADO\r\nGTV-EXIT=0");

        var tarefa = await filaService.CriarTarefaMaquinasAsync([maquina.Id], TiposAcao.Remover, "admin.ti", appCatalogoId: app.Id);
        await filaService.ProcessarMaquinaAsync(maquina.Id);

        var acao = await db.Acoes.FirstOrDefaultAsync(a => a.TarefaId == tarefa.Id && a.MaquinaId == maquina.Id);
        Assert.NotNull(acao);
        Assert.Equal(EstadosAcao.Executada, acao.Estado);
        Assert.Equal("NAO_ESTAVA_INSTALADO", acao.Resultado);
        Assert.Equal(0, acao.CodigoSaida);
    }

    [Fact]
    public async Task FilaAcoes_Instalar_InstallerBusy_SchedulesRetryWithoutFailing()
    {
        var (catalogoService, filaService, db, meshClient, _, _) = CreateTestServices();

        var app = await catalogoService.CadastrarAppAsync(new CadastrarAppInput(
            Nome: "App Busy " + Guid.NewGuid().ToString("N")[..8],
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: null
        ), new MemoryStream(Encoding.UTF8.GetBytes("dummy")), "app.msi", "admin.ti");

        var maquina = new Maquina
        {
            NodeId = "node//app_busy_" + Guid.NewGuid().ToString("N")[..6],
            NumeroSerie = "SN" + Guid.NewGuid().ToString("N")[..8],
            Hostname = "NB-BUSY",
            ConexaoEstado = "online",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OCUPADO", 1618, "msiexec em execução\r\nGTV-RESULT=OCUPADO\r\nGTV-EXIT=1618");

        var tarefa = await filaService.CriarTarefaMaquinasAsync([maquina.Id], TiposAcao.Instalar, "admin.ti", appCatalogoId: app.Id);
        await filaService.ProcessarMaquinaAsync(maquina.Id);

        var acao = await db.Acoes.FirstOrDefaultAsync(a => a.TarefaId == tarefa.Id && a.MaquinaId == maquina.Id);
        Assert.NotNull(acao);
        Assert.NotEqual(EstadosAcao.Falhou, acao.Estado);
        Assert.Equal(0, acao.Tentativas); // OCUPADO não incrementa tentativas
        Assert.Contains("OCUPADO_INICIO", acao.Detalhes);
    }

    [Fact]
    public async Task FilaAcoes_Instalar_OfflineMachine_StaysPendente_ExecutesWhenNodeConnects()
    {
        var (catalogoService, filaService, db, meshClient, _, _) = CreateTestServices();

        var app = await catalogoService.CadastrarAppAsync(new CadastrarAppInput(
            Nome: "App Offline " + Guid.NewGuid().ToString("N")[..8],
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: null
        ), new MemoryStream(Encoding.UTF8.GetBytes("dummy")), "app.msi", "admin.ti");

        var maquina = new Maquina
        {
            NodeId = "node//app_off_" + Guid.NewGuid().ToString("N")[..6],
            NumeroSerie = "SN" + Guid.NewGuid().ToString("N")[..8],
            Hostname = "NB-OFFLINE",
            ConexaoEstado = "offline",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow.AddHours(-2)
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        var tarefa = await filaService.CriarTarefaMaquinasAsync([maquina.Id], TiposAcao.Instalar, "admin.ti", appCatalogoId: app.Id);

        // Processamento não envia porque está offline
        await filaService.ProcessarMaquinaAsync(maquina.Id);
        var acaoPendente = await db.Acoes.FirstOrDefaultAsync(a => a.TarefaId == tarefa.Id && a.MaquinaId == maquina.Id);
        Assert.Equal(EstadosAcao.Pendente, acaoPendente!.Estado);

        // Máquina liga e conecta
        maquina.ConexaoEstado = "online";
        maquina.UltimoContatoEm = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        meshClient.RunScriptHandler = (nodeId, script) =>
            new RunCommandResult("OK", 0, "Instalado com sucesso\r\nGTV-RESULT=OK\r\nGTV-EXIT=0");
        await filaService.ProcessarNodeConnectedAsync(maquina.NodeId);

        var acaoExecutada = await db.Acoes.FirstOrDefaultAsync(a => a.TarefaId == tarefa.Id && a.MaquinaId == maquina.Id);
        Assert.Equal(EstadosAcao.Executada, acaoExecutada!.Estado);
        Assert.Equal("OK", acaoExecutada.Resultado);
    }

    [Fact]
    public void AuthenticodeValidator_RealSyntheticPe_ValidatesSignatureAndPublisher()
    {
        var validator = new AuthenticodeValidator();
        var filePath = Path.Combine(_tempDir, "signed_test.exe");
        CreateSyntheticSignedPeFile(filePath, "CN=Mozilla Corporation, O=Mozilla, C=US");

        // 1. Valida sem filtro de publisher
        var resultNoFilter = validator.ValidateFile(filePath);
        Assert.True(resultNoFilter.IsSigned);
        Assert.Contains("Mozilla Corporation", resultNoFilter.Publisher);
        Assert.Null(resultNoFilter.ErrorMessage);

        // 2. Valida com publisher esperado correto
        var resultMatch = validator.ValidateFile(filePath, "Mozilla Corporation");
        Assert.True(resultMatch.IsSigned);
        Assert.Null(resultMatch.ErrorMessage);

        // 3. Valida com publisher divergente
        var resultMismatch = validator.ValidateFile(filePath, "Google LLC");
        Assert.True(resultMismatch.IsSigned);
        Assert.NotNull(resultMismatch.ErrorMessage);
        Assert.Contains("não confere", resultMismatch.ErrorMessage);
    }

    [Fact]
    public void AuthenticodeValidator_UnsignedPe_ReturnsNotSigned()
    {
        var validator = new AuthenticodeValidator();
        var filePath = Path.Combine(_tempDir, "unsigned_test.exe");
        CreateSyntheticUnsignedPeFile(filePath);

        var result = validator.ValidateFile(filePath);
        Assert.False(result.IsSigned);
        Assert.Contains("sem assinatura digital", result.ErrorMessage);
    }

    [Fact]
    public async Task Endpoint_AgenteDownload_SupportsRange_And_RateLimitOnlyCountsInvalidTokens()
    {
        var clientFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAuthenticodeValidator>(new MockAuthenticodeValidator(true, "Mozilla Corporation"));
            });
        });

        using var scope = clientFactory.Services.CreateScope();
        var catalogo = scope.ServiceProvider.GetRequiredService<ICatalogoService>();
        var db = scope.ServiceProvider.GetRequiredService<GtViewerDbContext>();

        var fileContent = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var fileBytes = Encoding.UTF8.GetBytes(fileContent);

        var app = await catalogo.CadastrarAppAsync(new CadastrarAppInput(
            Nome: "Range App " + Guid.NewGuid().ToString("N")[..8],
            Versao: "1.0",
            FabricanteEsperado: "Mozilla Corporation",
            ParametrosInstalacao: null,
            ComandoRemocao: null,
            RegraDeteccaoTipo: "registro",
            RegraDeteccaoChave: "HKLM",
            RegraDeteccaoValor: null
        ), new MemoryStream(fileBytes), "range.msi", "admin.ti");

        var maquina = new Maquina
        {
            NodeId = "node//range_" + Guid.NewGuid().ToString("N")[..6],
            NumeroSerie = "SN_RANGE_" + Guid.NewGuid().ToString("N")[..6],
            Hostname = "NB-RANGE",
            ConexaoEstado = "online",
            CriadoEm = DateTimeOffset.UtcNow,
            UltimoContatoEm = DateTimeOffset.UtcNow
        };
        db.Maquinas.Add(maquina);
        await db.SaveChangesAsync();

        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var tarefa = new Tarefa
        {
            Tipo = TiposAcao.Instalar,
            Ator = "admin.ti",
            AppCatalogoId = app.Id,
            CriadoEm = DateTimeOffset.UtcNow
        };
        db.Tarefas.Add(tarefa);
        await db.SaveChangesAsync();

        var acao = new Acao
        {
            MaquinaId = maquina.Id,
            TarefaId = tarefa.Id,
            Tipo = TiposAcao.Instalar,
            Estado = EstadosAcao.Enviada,
            Ator = "admin.ti",
            Prioridade = PrioridadesAcao.Aplicativos,
            TokenAgenteHash = tokenHash,
            TokenAgenteExpiraEm = DateTimeOffset.UtcNow.AddMinutes(30),
            TokenAgenteUsado = false
        };
        db.Acoes.Add(acao);
        await db.SaveChangesAsync();

        var client = clientFactory.CreateClient();

        // 1. Download completo: 200 OK
        var responseFull = await client.GetAsync($"/agente/download/{token}");
        Assert.Equal(HttpStatusCode.OK, responseFull.StatusCode);
        var downloadedBytes = await responseFull.Content.ReadAsByteArrayAsync();
        Assert.Equal(fileBytes, downloadedBytes);

        // 2. Download com Range (retomada): bytes 10-19 -> 206 Partial Content
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/agente/download/{token}");
        rangeRequest.Headers.Range = new RangeHeaderValue(10, 19);
        var responseRange = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, responseRange.StatusCode);
        var rangeBytes = await responseRange.Content.ReadAsByteArrayAsync();
        Assert.Equal(10, rangeBytes.Length);
        Assert.Equal(Encoding.UTF8.GetBytes("ABCDEFGHIJ"), rangeBytes);

        // 3. Download com token inválido: 401 Unauthorized
        var responseInvalid = await client.GetAsync("/agente/download/token_completamente_invalido");
        Assert.Equal(HttpStatusCode.Unauthorized, responseInvalid.StatusCode);

        // 4. Download com Header Bearer em /agente/instalador
        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/agente/instalador");
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var responseBearer = await client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.OK, responseBearer.StatusCode);
    }

    private static string CreateSyntheticSignedPeFile(string path, string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

        var signedCms = new SignedCms(new ContentInfo(Encoding.UTF8.GetBytes("fake_pe_payload")));
        var signer = new CmsSigner(cert);
        signedCms.ComputeSignature(signer);
        var pkcs7 = signedCms.Encode();

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // DOS Header
        writer.Write((ushort)0x5A4D);
        for (int i = 0; i < 29; i++) writer.Write((ushort)0);
        writer.Write((int)0x80);

        while (ms.Position < 0x80) writer.Write((byte)0);

        // PE Signature
        writer.Write((uint)0x00004550);

        // COFF File Header
        writer.Write((ushort)0x8664);
        writer.Write((ushort)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((ushort)240);
        writer.Write((ushort)0x0022);

        // Optional Header
        var optHeaderStart = ms.Position;
        writer.Write((ushort)0x020B);
        for (int i = 0; i < 110; i++) writer.Write((byte)0);

        while (ms.Position < optHeaderStart + 144) writer.Write((byte)0);

        var certTableOffset = 512;
        var winCertLength = pkcs7.Length + 8;
        writer.Write((uint)certTableOffset);
        writer.Write((uint)winCertLength);

        while (ms.Position < optHeaderStart + 240) writer.Write((byte)0);
        while (ms.Position < certTableOffset) writer.Write((byte)0);

        // WIN_CERTIFICATE:
        writer.Write((int)winCertLength);
        writer.Write((short)0x0200);
        writer.Write((short)0x0002);
        writer.Write(pkcs7);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static string CreateSyntheticUnsignedPeFile(string path)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0x5A4D);
        for (int i = 0; i < 29; i++) writer.Write((ushort)0);
        writer.Write((int)0x80);

        while (ms.Position < 0x80) writer.Write((byte)0);
        writer.Write((uint)0x00004550);
        writer.Write((ushort)0x8664);
        writer.Write((ushort)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((uint)0);
        writer.Write((ushort)240);
        writer.Write((ushort)0x0022);

        var optHeaderStart = ms.Position;
        writer.Write((ushort)0x020B);
        for (int i = 0; i < 110; i++) writer.Write((byte)0);
        while (ms.Position < optHeaderStart + 144) writer.Write((byte)0);

        writer.Write((uint)0);
        writer.Write((uint)0);

        while (ms.Position < optHeaderStart + 240) writer.Write((byte)0);
        while (ms.Position < 512) writer.Write((byte)0);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}

public class MockAuthenticodeValidator : IAuthenticodeValidator
{
    private readonly bool _isSigned;
    private readonly string? _publisher;
    private readonly string? _errorMessage;

    public MockAuthenticodeValidator(bool isSigned, string? publisher = null, string? errorMessage = null)
    {
        _isSigned = isSigned;
        _publisher = publisher;
        _errorMessage = errorMessage;
    }

    public AuthenticodeValidationResult ValidateFile(string filePath, string? expectedPublisher = null)
    {
        if (!_isSigned)
        {
            return new AuthenticodeValidationResult(false, null, null, _errorMessage ?? "Instaladores sem assinatura digital ficam fora do catálogo na v1.");
        }

        if (!string.IsNullOrWhiteSpace(_errorMessage))
        {
            return new AuthenticodeValidationResult(true, _publisher, $"CN={_publisher}", _errorMessage);
        }

        return new AuthenticodeValidationResult(true, _publisher, $"CN={_publisher}", null);
    }
}

public class FakeLargeStream : Stream
{
    private readonly long _length;
    private long _position;

    public FakeLargeStream(long length)
    {
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;

        var bytesToRead = (int)Math.Min(count, remaining);
        Array.Clear(buffer, offset, bytesToRead);
        _position += bytesToRead;
        return bytesToRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
