using System.Data;
using ThinkingHome.Migrator.Framework;

// Ключ версионирования миграций этой библиотеки — своя история версий в общей БД.
[assembly: MigrationAssembly("prism.library")]

namespace Prism.Library.Migrations;

/// <summary>Создание таблицы метаданных. Плоское имя с префиксом Prism_, без схемы —
/// одинаково на SQLite и Postgres.</summary>
[Migration(1)]
public class Migration_1_Init : Migration
{
    public override void Apply()
    {
        Database.AddTable("Prism_MediaMetadata",
            new Column("MediaId", DbType.String, ColumnProperty.PrimaryKey),
            new Column("Kind", DbType.String, ColumnProperty.NotNull),
            new Column("Title", DbType.String),
            new Column("SeriesTitle", DbType.String),
            new Column("Season", DbType.Int32),
            new Column("Episode", DbType.Int32));
    }

    public override void Revert()
    {
        Database.RemoveTable("Prism_MediaMetadata");
    }
}
