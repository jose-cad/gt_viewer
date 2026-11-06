using GtViewer.Web.Data;
using GtViewer.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace GtViewer.Tests;

public class AuditoriaSecurityTests
{
    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtv_app;Password=gtv_app_secret_pass_2026!";
    private const string RetentionConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtv_retention;Password=gtv_retention_secret_pass_2026!";
    private const string AdminConnectionString =
        "Host=localhost;Port=5432;Database=gtviewer;Username=gtviewer;Password=gtviewer_dev_pwd";

    private static DbContextOptions<GtViewerDbContext> CreateOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<GtViewerDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    [Fact]
    public async Task GtvApp_CanInsertAndRead_Auditoria()
    {
        using var context = new GtViewerDbContext(CreateOptions(AppConnectionString));

        var log = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = "tecnico.teste",
            Tipo = "TESTE_INSERCAO_APP",
            Alvo = "PC-TESTE-01",
            Sucesso = true,
            DetalhesJson = "{\"teste\": true}",
            IpOrigem = "127.0.0.1"
        };

        context.Auditorias.Add(log);
        await context.SaveChangesAsync();

        Assert.True(log.Id > 0);

        var retrieved = await context.Auditorias.FirstOrDefaultAsync(a => a.Id == log.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("TESTE_INSERCAO_APP", retrieved.Tipo);
    }

    [Fact]
    public async Task GtvApp_CannotUpdate_Auditoria()
    {
        // 1. Inserir via gtv_app
        long insertedId;
        using (var insertContext = new GtViewerDbContext(CreateOptions(AppConnectionString)))
        {
            var log = new Auditoria
            {
                DataHora = DateTimeOffset.UtcNow,
                Ator = "tecnico.teste",
                Tipo = "TESTE_UPDATE_APP",
                Sucesso = true
            };
            insertContext.Auditorias.Add(log);
            await insertContext.SaveChangesAsync();
            insertedId = log.Id;
        }

        // 2. Tentar atualizar via gtv_app deve disparar violação de permissão (42501)
        using (var updateContext = new GtViewerDbContext(CreateOptions(AppConnectionString)))
        {
            var logToUpdate = await updateContext.Auditorias.FindAsync(insertedId);
            Assert.NotNull(logToUpdate);

            logToUpdate.Tipo = "TESTE_ALTERADO_ILEGALMENTE";

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => updateContext.SaveChangesAsync());
            var pgEx = ex.InnerException as PostgresException;
            Assert.NotNull(pgEx);
            Assert.Equal("42501", pgEx.SqlState); // insufficient_privilege
        }
    }

    [Fact]
    public async Task GtvApp_CannotDelete_Auditoria()
    {
        // 1. Inserir via gtv_app
        long insertedId;
        using (var insertContext = new GtViewerDbContext(CreateOptions(AppConnectionString)))
        {
            var log = new Auditoria
            {
                DataHora = DateTimeOffset.UtcNow,
                Ator = "tecnico.teste",
                Tipo = "TESTE_DELETE_APP",
                Sucesso = true
            };
            insertContext.Auditorias.Add(log);
            await insertContext.SaveChangesAsync();
            insertedId = log.Id;
        }

        // 2. Tentar deletar via gtv_app deve disparar violação de permissão (42501)
        using (var deleteContext = new GtViewerDbContext(CreateOptions(AppConnectionString)))
        {
            var logToDelete = await deleteContext.Auditorias.FindAsync(insertedId);
            Assert.NotNull(logToDelete);

            deleteContext.Auditorias.Remove(logToDelete);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
            var pgEx = ex.InnerException as PostgresException;
            Assert.NotNull(pgEx);
            Assert.Equal("42501", pgEx.SqlState); // insufficient_privilege
        }
    }

    [Fact]
    public async Task GtvRetention_CannotInsert_Auditoria()
    {
        using var retentionContext = new GtViewerDbContext(CreateOptions(RetentionConnectionString));

        var log = new Auditoria
        {
            DataHora = DateTimeOffset.UtcNow,
            Ator = "retencao.rotina",
            Tipo = "TESTE_INSERT_RETENTION",
            Sucesso = true
        };

        retentionContext.Auditorias.Add(log);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => retentionContext.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal("42501", pgEx.SqlState); // insufficient_privilege
    }

    [Fact]
    public async Task GtvRetention_CannotUpdate_Auditoria()
    {
        // 1. Inserir via gtv_app
        long insertedId;
        using (var insertContext = new GtViewerDbContext(CreateOptions(AppConnectionString)))
        {
            var log = new Auditoria
            {
                DataHora = DateTimeOffset.UtcNow,
                Ator = "tecnico.teste",
                Tipo = "TESTE_UPDATE_RETENTION",
                Sucesso = true
            };
            insertContext.Auditorias.Add(log);
            await insertContext.SaveChangesAsync();
            insertedId = log.Id;
        }

        // 2. Tentar atualizar via gtv_retention deve falhar com 42501
        using (var retentionContext = new GtViewerDbContext(CreateOptions(RetentionConnectionString)))
        {
            var logToUpdate = await retentionContext.Auditorias.FindAsync(insertedId);
            Assert.NotNull(logToUpdate);

            logToUpdate.Tipo = "TESTE_MODIFICADO_RETENCAO";

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => retentionContext.SaveChangesAsync());
            var pgEx = ex.InnerException as PostgresException;
            Assert.NotNull(pgEx);
            Assert.Equal("42501", pgEx.SqlState); // insufficient_privilege
        }
    }

    [Fact]
    public async Task GtvRetention_CanDelete_Auditoria()
    {
        // 1. Inserir registro com mais de 2 anos (ou registro de teste) via gtv_app
        long insertedId;
        using (var insertContext = new GtViewerDbContext(CreateOptions(AppConnectionString)))
        {
            var log = new Auditoria
            {
                DataHora = DateTimeOffset.UtcNow.AddYears(-3),
                Ator = "sistema.antigo",
                Tipo = "REGISTRO_RETENCAO_EXPIRADO",
                Sucesso = true
            };
            insertContext.Auditorias.Add(log);
            await insertContext.SaveChangesAsync();
            insertedId = log.Id;
        }

        // 2. Deletar via gtv_retention deve suceder!
        using (var retentionContext = new GtViewerDbContext(CreateOptions(RetentionConnectionString)))
        {
            var logToDelete = await retentionContext.Auditorias.FindAsync(insertedId);
            Assert.NotNull(logToDelete);

            retentionContext.Auditorias.Remove(logToDelete);
            var affected = await retentionContext.SaveChangesAsync();

            Assert.Equal(1, affected);
        }

        // 3. Confirmar que o registro foi removido
        using (var verifyContext = new GtViewerDbContext(CreateOptions(AppConnectionString)))
        {
            var remaining = await verifyContext.Auditorias.FindAsync(insertedId);
            Assert.Null(remaining);
        }
    }

    [Fact]
    public async Task GtvApp_CanPerformFullCrudOnOperationalEntities()
    {
        using var context = new GtViewerDbContext(CreateOptions(AppConnectionString));

        var grupo = new Grupo
        {
            Nome = $"Grupo-CRUD-{Guid.NewGuid():N}",
            Descricao = "Grupo de teste de CRUD operacional"
        };

        // CREATE
        context.Grupos.Add(grupo);
        await context.SaveChangesAsync();
        Assert.True(grupo.Id > 0);

        // READ
        var retrieved = await context.Grupos.FindAsync(grupo.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(grupo.Nome, retrieved.Nome);

        // UPDATE
        retrieved.Descricao = "Descricao atualizada com sucesso pelo gtv_app";
        await context.SaveChangesAsync();

        // DELETE
        context.Grupos.Remove(retrieved);
        await context.SaveChangesAsync();

        var deleted = await context.Grupos.FindAsync(grupo.Id);
        Assert.Null(deleted);
    }
}
