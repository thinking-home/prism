using System.Data;
using ThinkingHome.Migrator.Framework;

namespace Prism.Plugins.Library.Migrations;

/// <summary>
/// Идентификаторы групп становятся GUID: id генерируется кодом без обращения к БД,
/// поэтому вся раскладка файла (группы + членство + мета) пишется одним сохранением.
/// Таблицы дерева пересоздаются без переноса данных (фича не опубликована,
/// наполнение восстановимо правилами/руками); мета групп со старыми числовыми
/// ключами вычищается.
/// </summary>
[Migration(3)]
public class Migration_3_NodeGuidIds : Migration
{
    public override void Apply()
    {
        Database.RemoveTable("Prism_NodeItem");
        Database.RemoveTable("Prism_Node");

        Database.AddTable("Prism_Node",
            new Column("Id", DbType.Guid, ColumnProperty.PrimaryKey),
            new Column("ParentId", DbType.Guid),
            new Column("Name", DbType.String, ColumnProperty.NotNull));

        Database.AddTable("Prism_NodeItem",
            new Column("NodeId", DbType.Guid, ColumnProperty.PrimaryKey),
            new Column("FileKey", DbType.String, ColumnProperty.PrimaryKey));

        Database.ExecuteNonQuery("DELETE FROM \"Prism_Meta\" WHERE \"EntityType\" = 'node'");
    }

    public override void Revert()
    {
        Database.RemoveTable("Prism_NodeItem");
        Database.RemoveTable("Prism_Node");

        Database.AddTable("Prism_Node",
            new Column("Id", DbType.Int64, ColumnProperty.PrimaryKeyWithIdentity),
            new Column("ParentId", DbType.Int64),
            new Column("Name", DbType.String, ColumnProperty.NotNull));

        Database.AddTable("Prism_NodeItem",
            new Column("NodeId", DbType.Int64, ColumnProperty.PrimaryKey),
            new Column("FileKey", DbType.String, ColumnProperty.PrimaryKey));

        Database.ExecuteNonQuery("DELETE FROM \"Prism_Meta\" WHERE \"EntityType\" = 'node'");
    }
}
