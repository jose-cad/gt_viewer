using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;

namespace GtViewer.Web.Services;

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
}
