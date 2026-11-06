using GtViewer.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GtViewer.Web.Data;

public class GtViewerDbContext : DbContext
{
    public GtViewerDbContext(DbContextOptions<GtViewerDbContext> options) : base(options)
    {
    }

    public DbSet<Tecnico> Tecnicos => Set<Tecnico>();
    public DbSet<Maquina> Maquinas => Set<Maquina>();
    public DbSet<HistoricoUsuarioMaquina> HistoricoUsuarioMaquinas => Set<HistoricoUsuarioMaquina>();
    public DbSet<Grupo> Grupos => Set<Grupo>();
    public DbSet<Acao> Acoes => Set<Acao>();
    public DbSet<Tarefa> Tarefas => Set<Tarefa>();
    public DbSet<AppCatalogo> AppsCatalogo => Set<AppCatalogo>();
    public DbSet<ContratoImportacao> ContratosImportacao => Set<ContratoImportacao>();
    public DbSet<ContratoSerial> ContratosSeriais => Set<ContratoSerial>();
    public DbSet<Auditoria> Auditorias => Set<Auditoria>();
    public DbSet<TravaSessao> TravasSessao => Set<TravaSessao>();
    public DbSet<AlertaEmergencia> AlertasEmergencia => Set<AlertaEmergencia>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Tecnico
        modelBuilder.Entity<Tecnico>(b =>
        {
            b.ToTable("Tecnicos");
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.UsuarioDominio).IsUnique();
            b.Property(t => t.UsuarioDominio).HasMaxLength(100).IsRequired();
            b.Property(t => t.NomeExibicao).HasMaxLength(200).IsRequired();
            b.Property(t => t.Email).HasMaxLength(256);
            b.Property(t => t.Permissoes).HasMaxLength(500).IsRequired();
            b.Property(t => t.CodigoAtivacaoHash).HasMaxLength(128);
            b.Property(t => t.SenhaLocalHash).HasMaxLength(256);
        });

        // 2. Maquina
        modelBuilder.Entity<Maquina>(b =>
        {
            b.ToTable("Maquinas");
            b.HasKey(m => m.Id);
            b.HasIndex(m => m.NumeroSerie).IsUnique();
            b.HasIndex(m => m.NodeId);
            b.HasIndex(m => m.ConexaoEstado);
            b.HasIndex(m => m.BloqueioEstado);

            b.Property(m => m.NumeroSerie).HasMaxLength(100).IsRequired();
            b.Property(m => m.NodeId).HasMaxLength(128);
            b.Property(m => m.Hostname).HasMaxLength(200);
            b.Property(m => m.Modelo).HasMaxLength(200);
            b.Property(m => m.Fabricante).HasMaxLength(200);
            b.Property(m => m.SistemaOperacional).HasMaxLength(200);
            b.Property(m => m.Arquitetura).HasMaxLength(50);
            b.Property(m => m.AntivirusNome).HasMaxLength(200);
            b.Property(m => m.ConexaoEstado).HasMaxLength(50).IsRequired();
            b.Property(m => m.BloqueioEstado).HasMaxLength(50).IsRequired();

            b.HasOne(m => m.Grupo)
                .WithMany(g => g.Maquinas)
                .HasForeignKey(m => m.GrupoId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(m => m.TravaSessao)
                .WithOne(t => t.Maquina)
                .HasForeignKey<TravaSessao>(t => t.MaquinaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 3. HistoricoUsuarioMaquina
        modelBuilder.Entity<HistoricoUsuarioMaquina>(b =>
        {
            b.ToTable("HistoricoUsuarioMaquinas");
            b.HasKey(h => h.Id);
            b.HasIndex(h => h.MaquinaId);
            b.HasIndex(h => h.Usuario);
            b.HasIndex(h => h.InicioEm);

            b.Property(h => h.Usuario).HasMaxLength(100).IsRequired();

            b.HasOne(h => h.Maquina)
                .WithMany(m => m.HistoricoUsuarios)
                .HasForeignKey(h => h.MaquinaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 4. Grupo
        modelBuilder.Entity<Grupo>(b =>
        {
            b.ToTable("Grupos");
            b.HasKey(g => g.Id);
            b.HasIndex(g => g.Nome).IsUnique();
            b.Property(g => g.Nome).HasMaxLength(100).IsRequired();
            b.Property(g => g.Descricao).HasMaxLength(500);
        });

        // 5. Acao
        modelBuilder.Entity<Acao>(b =>
        {
            b.ToTable("Acoes");
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.MaquinaId);
            b.HasIndex(a => a.TarefaId);
            b.HasIndex(a => a.Estado);
            b.HasIndex(a => a.Prioridade);
            b.HasIndex(a => a.CriadoEm);

            b.Property(a => a.Tipo).HasMaxLength(50).IsRequired();
            b.Property(a => a.Estado).HasMaxLength(50).IsRequired();
            b.Property(a => a.Ator).HasMaxLength(100).IsRequired();
            b.Property(a => a.Resultado).HasMaxLength(100);
            b.Property(a => a.TokenAgenteHash).HasMaxLength(128);

            b.HasOne(a => a.Maquina)
                .WithMany(m => m.Acoes)
                .HasForeignKey(a => a.MaquinaId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(a => a.Tarefa)
                .WithMany(t => t.Acoes)
                .HasForeignKey(a => a.TarefaId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // 6. Tarefa
        modelBuilder.Entity<Tarefa>(b =>
        {
            b.ToTable("Tarefas");
            b.HasKey(t => t.Id);
            b.Property(t => t.Tipo).HasMaxLength(50).IsRequired();
            b.Property(t => t.Ator).HasMaxLength(100).IsRequired();

            b.HasOne(t => t.AppCatalogo)
                .WithMany()
                .HasForeignKey(t => t.AppCatalogoId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(t => t.Grupo)
                .WithMany()
                .HasForeignKey(t => t.GrupoId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // 7. AppCatalogo
        modelBuilder.Entity<AppCatalogo>(b =>
        {
            b.ToTable("AppsCatalogo");
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.Nome).IsUnique();
            b.Property(a => a.Nome).HasMaxLength(150).IsRequired();
            b.Property(a => a.Versao).HasMaxLength(50).IsRequired();
            b.Property(a => a.FabricanteEsperado).HasMaxLength(200).IsRequired();
            b.Property(a => a.HashSha256).HasMaxLength(64).IsRequired();
            b.Property(a => a.NomeArquivo).HasMaxLength(255).IsRequired();
            b.Property(a => a.ParametrosInstalacao).HasMaxLength(500);
            b.Property(a => a.ComandoRemocao).HasMaxLength(500);
            b.Property(a => a.RegraDeteccaoTipo).HasMaxLength(50).IsRequired();
            b.Property(a => a.RegraDeteccaoChave).HasMaxLength(500).IsRequired();
            b.Property(a => a.RegraDeteccaoValor).HasMaxLength(500);
        });

        // 8. ContratoImportacao
        modelBuilder.Entity<ContratoImportacao>(b =>
        {
            b.ToTable("ContratosImportacao");
            b.HasKey(c => c.Id);
            b.HasIndex(c => c.Vigente);
            b.Property(c => c.NomeArquivo).HasMaxLength(255).IsRequired();
            b.Property(c => c.Ator).HasMaxLength(100).IsRequired();
        });

        // 9. ContratoSerial
        modelBuilder.Entity<ContratoSerial>(b =>
        {
            b.ToTable("ContratosSeriais");
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.ContratoImportacaoId);
            b.HasIndex(s => s.NumeroSerie);
            b.Property(s => s.NumeroSerie).HasMaxLength(100).IsRequired();

            b.HasOne(s => s.ContratoImportacao)
                .WithMany(c => c.Seriais)
                .HasForeignKey(s => s.ContratoImportacaoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 10. Auditoria (Tabela imutável: apenas INSERT e SELECT para aplicação, DELETE só para retenção)
        modelBuilder.Entity<Auditoria>(b =>
        {
            b.ToTable("Auditorias");
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.DataHora);
            b.HasIndex(a => a.Ator);
            b.HasIndex(a => a.Tipo);
            b.HasIndex(a => a.Alvo);
            b.HasIndex(a => a.MaquinaId);
            b.HasIndex(a => a.AcaoId);

            b.Property(a => a.Ator).HasMaxLength(100).IsRequired();
            b.Property(a => a.Tipo).HasMaxLength(100).IsRequired();
            b.Property(a => a.Alvo).HasMaxLength(200);
            b.Property(a => a.IpOrigem).HasMaxLength(50);
        });

        // 11. TravaSessao
        modelBuilder.Entity<TravaSessao>(b =>
        {
            b.ToTable("TravasSessao");
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.MaquinaId).IsUnique();
            b.HasIndex(t => t.ExpiraEm);

            b.Property(t => t.TecnicoUsername).HasMaxLength(100).IsRequired();
            b.Property(t => t.IdentificadorSessao).HasMaxLength(64).IsRequired();
            b.Property(t => t.UsuarioLogadoNoMomento).HasMaxLength(100).IsRequired();
        });

        // 12. AlertaEmergencia
        modelBuilder.Entity<AlertaEmergencia>(b =>
        {
            b.ToTable("AlertasEmergencia");
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.DataHora);
            b.HasIndex(a => a.Reconhecido);

            b.Property(a => a.Ator).HasMaxLength(100).IsRequired();
            b.Property(a => a.Mensagem).HasMaxLength(1000).IsRequired();
            b.Property(a => a.ReconhecidoPor).HasMaxLength(100);
        });
    }
}
