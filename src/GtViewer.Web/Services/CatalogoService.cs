using System.Security.Cryptography;
using System.Text.Json;
using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using GtViewer.Web.Models.FilaAcoes;
using GtViewer.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GtViewer.Web.Services;

public class CatalogoService : ICatalogoService
{
    private const long MaxFileSizeBytes = 500L * 1024 * 1024; // 500 MB
    private readonly GtViewerDbContext _db;
    private readonly IAuthenticodeValidator _authenticodeValidator;
    private readonly ILogger<CatalogoService> _logger;
    private readonly string _storagePath;

    public CatalogoService(
        GtViewerDbContext db,
        IAuthenticodeValidator authenticodeValidator,
        IConfiguration configuration,
        ILogger<CatalogoService> logger)
    {
        _db = db;
        _authenticodeValidator = authenticodeValidator;
        _logger = logger;

        var configuredPath = configuration["Catalogo:StoragePath"];
        _storagePath = !string.IsNullOrWhiteSpace(configuredPath)
            ? (Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(AppContext.BaseDirectory, configuredPath))
            : Path.Combine(AppContext.BaseDirectory, "uploads", "catalogo");

        Directory.CreateDirectory(_storagePath);
    }

    public async Task<List<AppCatalogo>> ListarAppsAsync(bool apenasAtivos = false, CancellationToken cancellationToken = default)
    {
        var query = _db.AppsCatalogo.AsNoTracking();
        if (apenasAtivos)
        {
            query = query.Where(a => a.Ativo);
        }
        return await query.OrderBy(a => a.Nome).ToListAsync(cancellationToken);
    }

    public async Task<AppCatalogo?> ObterAppPorIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _db.AppsCatalogo.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<AppCatalogo> CadastrarAppAsync(
        CadastrarAppInput input,
        Stream arquivoStream,
        string nomeArquivoOriginal,
        string ator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(arquivoStream);
        if (string.IsNullOrWhiteSpace(nomeArquivoOriginal))
        {
            throw new ArgumentException("Nome do arquivo não pode ser vazio.", nameof(nomeArquivoOriginal));
        }

        var ext = Path.GetExtension(nomeArquivoOriginal).ToLowerInvariant();
        if (ext != ".msi" && ext != ".exe")
        {
            throw new InvalidOperationException("Formato inválido. Apenas instaladores .msi ou .exe são aceitos no catálogo.");
        }

        var nomeExiste = await _db.AppsCatalogo.AnyAsync(a => a.Nome.ToLower() == input.Nome.ToLower(), cancellationToken);
        if (nomeExiste)
        {
            throw new InvalidOperationException($"Já existe um aplicativo cadastrado com o nome '{input.Nome}'.");
        }

        // Salva arquivo em disco e calcula Hash SHA-256
        var savedFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(nomeArquivoOriginal)}";
        var fullPath = Path.Combine(_storagePath, savedFileName);

        string hashSha256;
        long totalBytes = 0;

        try
        {
            using (var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await arquivoStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaxFileSizeBytes)
                    {
                        throw new InvalidOperationException("O instalador excede o limite máximo permitido de 500 MB.");
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    sha.TransformBlock(buffer, 0, bytesRead, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hashSha256 = Convert.ToHexStringLower(sha.Hash!);
            }

            // Validação de assinatura Authenticode e Fabricante
            var validation = _authenticodeValidator.ValidateFile(fullPath, input.FabricanteEsperado);
            if (!validation.IsSigned)
            {
                throw new InvalidOperationException(validation.ErrorMessage ?? "Instaladores sem assinatura digital ficam fora do catálogo na v1.");
            }

            if (!string.IsNullOrWhiteSpace(validation.ErrorMessage))
            {
                throw new InvalidOperationException(validation.ErrorMessage);
            }
        }
        catch
        {
            if (File.Exists(fullPath))
            {
                try { File.Delete(fullPath); } catch { /* ignore */ }
            }
            throw;
        }

        var app = new AppCatalogo
        {
            Nome = input.Nome.Trim(),
            Versao = input.Versao.Trim(),
            FabricanteEsperado = input.FabricanteEsperado.Trim(),
            HashSha256 = hashSha256,
            NomeArquivo = savedFileName,
            TamanhoBytes = totalBytes,
            ParametrosInstalacao = input.ParametrosInstalacao?.Trim() ?? string.Empty,
            ComandoRemocao = input.ComandoRemocao?.Trim() ?? string.Empty,
            RegraDeteccaoTipo = input.RegraDeteccaoTipo.Trim().ToLowerInvariant(),
            RegraDeteccaoChave = input.RegraDeteccaoChave.Trim(),
            RegraDeteccaoValor = input.RegraDeteccaoValor?.Trim(),
            Ativo = true,
            CriadoEm = DateTimeOffset.UtcNow,
            AtualizadoEm = DateTimeOffset.UtcNow
        };

        // Auditoria e gravação atômica (Invariante 5)
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        _db.AppsCatalogo.Add(app);
        await _db.SaveChangesAsync(cancellationToken);

        var audit = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = ator,
            Tipo = TiposAuditoria.AlterarCatalogo,
            Alvo = app.Nome,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new
            {
                operacao = "criar",
                appId = app.Id,
                app.Nome,
                app.Versao,
                app.FabricanteEsperado,
                app.HashSha256,
                app.TamanhoBytes
            })
        };
        _db.Auditorias.Add(audit);
        await _db.SaveChangesAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        _logger.LogInformation("[Catalogo] Aplicativo '{Nome}' v{Versao} cadastrado com sucesso por {Ator}.", app.Nome, app.Versao, ator);
        return app;
    }

    public async Task<AppCatalogo> AtualizarAppAsync(
        int id,
        AtualizarAppInput input,
        string ator,
        Stream? novoArquivoStream = null,
        string? nomeArquivoOriginal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var app = await _db.AppsCatalogo.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (app == null)
        {
            throw new KeyNotFoundException($"Aplicativo com ID {id} não encontrado no catálogo.");
        }

        var nomeExiste = await _db.AppsCatalogo.AnyAsync(a => a.Id != id && a.Nome.ToLower() == input.Nome.ToLower(), cancellationToken);
        if (nomeExiste)
        {
            throw new InvalidOperationException($"Já existe outro aplicativo cadastrado com o nome '{input.Nome}'.");
        }

        string? novoArquivoSalvo = null;
        string? novoHash = null;
        long? novoTamanho = null;

        if (novoArquivoStream != null && !string.IsNullOrWhiteSpace(nomeArquivoOriginal))
        {
            var ext = Path.GetExtension(nomeArquivoOriginal).ToLowerInvariant();
            if (ext != ".msi" && ext != ".exe")
            {
                throw new InvalidOperationException("Formato inválido. Apenas instaladores .msi ou .exe são aceitos no catálogo.");
            }

            var savedFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(nomeArquivoOriginal)}";
            var fullPath = Path.Combine(_storagePath, savedFileName);

            long totalBytes = 0;
            try
            {
                using (var fileStream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await novoArquivoStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        totalBytes += bytesRead;
                        if (totalBytes > MaxFileSizeBytes)
                        {
                            throw new InvalidOperationException("O instalador excede o limite máximo permitido de 500 MB.");
                        }

                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        sha.TransformBlock(buffer, 0, bytesRead, null, 0);
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    novoHash = Convert.ToHexStringLower(sha.Hash!);
                    novoTamanho = totalBytes;
                }

                var validation = _authenticodeValidator.ValidateFile(fullPath, input.FabricanteEsperado);
                if (!validation.IsSigned)
                {
                    throw new InvalidOperationException(validation.ErrorMessage ?? "Instaladores sem assinatura digital ficam fora do catálogo na v1.");
                }

                if (!string.IsNullOrWhiteSpace(validation.ErrorMessage))
                {
                    throw new InvalidOperationException(validation.ErrorMessage);
                }

                novoArquivoSalvo = savedFileName;
            }
            catch
            {
                if (File.Exists(fullPath))
                {
                    try { File.Delete(fullPath); } catch { /* ignore */ }
                }
                throw;
            }
        }

        var arquivoAntigoParaRemover = novoArquivoSalvo != null ? app.NomeArquivo : null;

        app.Nome = input.Nome.Trim();
        app.Versao = input.Versao.Trim();
        app.FabricanteEsperado = input.FabricanteEsperado.Trim();
        app.ParametrosInstalacao = input.ParametrosInstalacao?.Trim() ?? string.Empty;
        app.ComandoRemocao = input.ComandoRemocao?.Trim() ?? string.Empty;
        app.RegraDeteccaoTipo = input.RegraDeteccaoTipo.Trim().ToLowerInvariant();
        app.RegraDeteccaoChave = input.RegraDeteccaoChave.Trim();
        app.RegraDeteccaoValor = input.RegraDeteccaoValor?.Trim();
        app.Ativo = input.Ativo;
        app.AtualizadoEm = DateTimeOffset.UtcNow;

        if (novoArquivoSalvo != null)
        {
            app.NomeArquivo = novoArquivoSalvo;
            app.HashSha256 = novoHash!;
            app.TamanhoBytes = novoTamanho!.Value;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var audit = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = ator,
            Tipo = TiposAuditoria.AlterarCatalogo,
            Alvo = app.Nome,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new
            {
                operacao = "atualizar",
                appId = app.Id,
                app.Nome,
                app.Versao,
                app.FabricanteEsperado,
                app.Ativo,
                novoArquivo = novoArquivoSalvo != null
            })
        };
        _db.Auditorias.Add(audit);
        await _db.SaveChangesAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);

        if (arquivoAntigoParaRemover != null)
        {
            var oldPath = Path.Combine(_storagePath, arquivoAntigoParaRemover);
            if (File.Exists(oldPath))
            {
                try { File.Delete(oldPath); } catch { /* ignore */ }
            }
        }

        _logger.LogInformation("[Catalogo] Aplicativo '{Nome}' v{Versao} atualizado por {Ator}.", app.Nome, app.Versao, ator);
        return app;
    }

    public async Task<bool> DesativarAppAsync(int id, string ator, CancellationToken cancellationToken = default)
    {
        var app = await _db.AppsCatalogo.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (app == null) return false;

        app.Ativo = false;
        app.AtualizadoEm = DateTimeOffset.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var audit = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = ator,
            Tipo = TiposAuditoria.AlterarCatalogo,
            Alvo = app.Nome,
            Sucesso = true,
            DetalhesJson = JsonSerializer.Serialize(new
            {
                operacao = "desativar",
                appId = app.Id,
                app.Nome
            })
        };
        _db.Auditorias.Add(audit);
        await _db.SaveChangesAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        _logger.LogInformation("[Catalogo] Aplicativo '{Nome}' desativado por {Ator}.", app.Nome, ator);
        return true;
    }

    public async Task<string?> ObterCaminhoFisicoAppAsync(int id, CancellationToken cancellationToken = default)
    {
        var app = await _db.AppsCatalogo.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (app == null) return null;

        var fullPath = Path.Combine(_storagePath, app.NomeArquivo);
        return File.Exists(fullPath) ? fullPath : null;
    }

    public async Task<FileStreamResultInfo?> ObterInstaladorPorTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var tokenHash = ComputeSha256(token);
        var agora = DateTimeOffset.UtcNow;

        // Busca ação com o token correspondente
        var acao = await _db.Acoes
            .Include(a => a.Tarefa)
            .FirstOrDefaultAsync(a => a.TokenAgenteHash == tokenHash, cancellationToken);

        if (acao == null) return null;

        // Token expira em 30 min da ação
        if (acao.TokenAgenteExpiraEm == null || acao.TokenAgenteExpiraEm < agora)
        {
            return null;
        }

        // Apenas ações de instalação podem baixar o instalador
        if (acao.Tipo != TiposAcao.Instalar)
        {
            return null;
        }

        int? appCatalogoId = acao.Tarefa?.AppCatalogoId;
        if (appCatalogoId == null && !string.IsNullOrWhiteSpace(acao.ParametrosJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(acao.ParametrosJson);
                if (doc.RootElement.TryGetProperty("appCatalogoId", out var idElem))
                {
                    appCatalogoId = idElem.GetInt32();
                }
            }
            catch
            {
                // ignore
            }
        }

        if (appCatalogoId == null) return null;

        var app = await _db.AppsCatalogo.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appCatalogoId.Value, cancellationToken);
        if (app == null) return null;

        var fullPath = Path.Combine(_storagePath, app.NomeArquivo);
        if (!File.Exists(fullPath)) return null;

        var fileInfo = new FileInfo(fullPath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var contentType = Path.GetExtension(fullPath).Equals(".msi", StringComparison.OrdinalIgnoreCase)
            ? "application/x-msi"
            : "application/octet-stream";

        return new FileStreamResultInfo(stream, contentType, app.NomeArquivo, fileInfo.Length);
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
