using GtViewer.Web.Data.Entities;

namespace GtViewer.Web.Services;

public record CadastrarAppInput(
    string Nome,
    string Versao,
    string FabricanteEsperado,
    string? ParametrosInstalacao,
    string? ComandoRemocao,
    string RegraDeteccaoTipo,
    string RegraDeteccaoChave,
    string? RegraDeteccaoValor
);

public record AtualizarAppInput(
    string Nome,
    string Versao,
    string FabricanteEsperado,
    string? ParametrosInstalacao,
    string? ComandoRemocao,
    string RegraDeteccaoTipo,
    string RegraDeteccaoChave,
    string? RegraDeteccaoValor,
    bool Ativo
);

public record FileStreamResultInfo(
    Stream Stream,
    string ContentType,
    string FileName,
    long FileLength
);

public interface ICatalogoService
{
    Task<List<AppCatalogo>> ListarAppsAsync(bool apenasAtivos = false, CancellationToken cancellationToken = default);
    Task<AppCatalogo?> ObterAppPorIdAsync(int id, CancellationToken cancellationToken = default);
    Task<AppCatalogo> CadastrarAppAsync(CadastrarAppInput input, Stream arquivoStream, string nomeArquivoOriginal, string ator, CancellationToken cancellationToken = default);
    Task<AppCatalogo> AtualizarAppAsync(int id, AtualizarAppInput input, string ator, Stream? novoArquivoStream = null, string? nomeArquivoOriginal = null, CancellationToken cancellationToken = default);
    Task<bool> DesativarAppAsync(int id, string ator, CancellationToken cancellationToken = default);
    Task<string?> ObterCaminhoFisicoAppAsync(int id, CancellationToken cancellationToken = default);
    Task<FileStreamResultInfo?> ObterInstaladorPorTokenAsync(string token, CancellationToken cancellationToken = default);
}
