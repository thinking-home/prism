using System.Data;
using ThinkingHome.Migrator.Framework;

namespace Prism.Plugins.Library.Migrations;

/// <summary>
/// Структурирование библиотеки: дерево групп (Prism_Node), членство файлов по
/// ключу содержимого (Prism_NodeItem) и свободная мета ключ-значение (Prism_Meta).
/// Старая плоская таблица метаданных удаляется — её заменяет Prism_Meta
/// (данных в ней не было, переносить нечего).
/// </summary>
[Migration(2)]
public class Migration_2_LibraryStructure : Migration
{
    public override void Apply()
    {
        Database.AddTable("Prism_Node",
            new Column("Id", DbType.Int64, ColumnProperty.PrimaryKeyWithIdentity),
            new Column("ParentId", DbType.Int64),
            new Column("Name", DbType.String, ColumnProperty.NotNull));

        Database.AddTable("Prism_NodeItem",
            new Column("NodeId", DbType.Int64, ColumnProperty.PrimaryKey),
            new Column("FileKey", DbType.String, ColumnProperty.PrimaryKey));

        Database.AddTable("Prism_Meta",
            new Column("EntityType", DbType.String, ColumnProperty.PrimaryKey),
            new Column("EntityKey", DbType.String, ColumnProperty.PrimaryKey),
            new Column("Key", DbType.String, ColumnProperty.PrimaryKey),
            new Column("Value", DbType.String, ColumnProperty.NotNull));

        Database.RemoveTable("Prism_MediaMetadata");
    }

    public override void Revert()
    {
        Database.RemoveTable("Prism_Meta");
        Database.RemoveTable("Prism_NodeItem");
        Database.RemoveTable("Prism_Node");

        Database.AddTable("Prism_MediaMetadata",
            new Column("MediaId", DbType.String, ColumnProperty.PrimaryKey),
            new Column("Kind", DbType.String, ColumnProperty.NotNull),
            new Column("Title", DbType.String),
            new Column("SeriesTitle", DbType.String),
            new Column("Season", DbType.Int32),
            new Column("Episode", DbType.Int32));
    }
}
