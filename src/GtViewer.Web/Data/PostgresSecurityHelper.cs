using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Data;

public static class PostgresSecurityHelper
{
    public static string GenerateGrantSql(string appUser = "gtv_app", string retentionUser = "gtv_retention")
    {
        return $@"
-- Permissões de uso no schema public
GRANT USAGE ON SCHEMA public TO {appUser}, {retentionUser};

-- Usuário da Aplicação (gtv_app):
-- Leitura e escrita completa nas entidades operacionais
GRANT ALL PRIVILEGES ON TABLE ""Tecnicos"", ""Maquinas"", ""HistoricoUsuarioMaquinas"", ""Grupos"",
    ""Acoes"", ""Tarefas"", ""AppsCatalogo"", ""ContratosImportacao"", ""ContratosSeriais"",
    ""TravasSessao"", ""AlertasEmergencia"" TO {appUser};

-- Sequências de chaves primárias
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {appUser};

-- Leitura de metadados de migrações
GRANT SELECT ON TABLE ""__EFMigrationsHistory"" TO {appUser};

-- Regra de Segurança Estrita em Auditoria para gtv_app (Invariante 5 e Tarefa 5):
-- Apenas INSERT e SELECT. Bloqueio imperativo de UPDATE e DELETE no nível de permissão do PostgreSQL.
REVOKE ALL ON TABLE ""Auditorias"" FROM {appUser};
GRANT SELECT, INSERT ON TABLE ""Auditorias"" TO {appUser};
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE ""Auditorias"" FROM {appUser};

-- Usuário da Rotina de Retenção (gtv_retention) (Invariante 12 e Tarefa 5):
-- Único com permissão de DELETE em Auditorias (retenção de 2 anos).
-- Sem permissão de INSERT ou UPDATE em Auditorias.
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {retentionUser};
GRANT SELECT, DELETE ON TABLE ""Auditorias"" TO {retentionUser};
GRANT SELECT, DELETE ON TABLE ""HistoricoUsuarioMaquinas"" TO {retentionUser};
GRANT SELECT, DELETE ON TABLE ""Acoes"" TO {retentionUser};
REVOKE INSERT, UPDATE, TRUNCATE ON TABLE ""Auditorias"" FROM {retentionUser};
";
    }

    public static async Task ApplySecurityGrantsAsync(DbContext dbContext, string appUser = "gtv_app", string retentionUser = "gtv_retention")
    {
        var sql = GenerateGrantSql(appUser, retentionUser);
        await dbContext.Database.ExecuteSqlRawAsync(sql);
    }
}
