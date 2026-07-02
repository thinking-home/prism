using Microsoft.EntityFrameworkCore;

namespace Prism.Plugins.Library;

/// <summary>
/// EF Core контекст плагина. Провайдер (SQLite/Postgres) выбирается при регистрации
/// по конфигу — сам контекст про конкретную СУБД не знает. Таблицы плоские, с
/// префиксом Prism_ (без схемы), чтобы не конфликтовать в общей БД со смарт-домом.
/// </summary>
public class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<MediaMetadataRecord> Metadata => Set<MediaMetadataRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MediaMetadataRecord>(e =>
        {
            e.ToTable("Prism_MediaMetadata");
            e.HasKey(x => x.MediaId);
        });
    }
}
