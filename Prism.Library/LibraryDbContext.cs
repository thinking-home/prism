using Microsoft.EntityFrameworkCore;

namespace Prism.Library;

/// <summary>
/// EF Core контекст плагина. Провайдер (SQLite/Postgres) выбирается при регистрации
/// по конфигу — сам контекст про конкретную СУБД не знает. Таблицы плоские, с
/// префиксом Prism_ (без схемы), чтобы не конфликтовать в общей БД со смарт-домом.
/// </summary>
public class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<LibraryNode> Nodes => Set<LibraryNode>();
    public DbSet<NodeItemRecord> NodeItems => Set<NodeItemRecord>();
    public DbSet<MetaRecord> Meta => Set<MetaRecord>();
    public DbSet<FileHostRecord> FileHosts => Set<FileHostRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LibraryNode>(e =>
        {
            e.ToTable("Prism_Node");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); // GUID назначает код
        });
        b.Entity<NodeItemRecord>(e =>
        {
            e.ToTable("Prism_NodeItem");
            e.HasKey(x => new { x.NodeId, x.FileKey });
        });
        b.Entity<MetaRecord>(e =>
        {
            e.ToTable("Prism_Meta");
            e.HasKey(x => new { x.EntityType, x.EntityKey, x.Key });
        });
        b.Entity<FileHostRecord>(e =>
        {
            e.ToTable("Prism_FileHost");
            e.HasKey(x => new { x.FileKey, x.Host });
        });
    }
}
