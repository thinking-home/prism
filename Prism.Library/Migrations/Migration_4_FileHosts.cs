using System.Data;
using ThinkingHome.Migrator.Framework;

namespace Prism.Library.Migrations;

/// <summary>
/// Бухгалтерия «id файла → хост»: какие хосты видели файл. По ней преемник
/// осиротевшего id спрашивается только у хостов, которые файл знали, а не у
/// всех (меньше запросов и точек отказа). Строки переживают исчезновение
/// файла — это и есть память для ремапа; чистка — вместе с записями файла в gc.
/// </summary>
[Migration(4)]
public class Migration_4_FileHosts : Migration
{
    public override void Apply()
    {
        Database.AddTable("Prism_FileHost",
            new Column("FileKey", DbType.String, ColumnProperty.PrimaryKey),
            new Column("Host", DbType.String, ColumnProperty.PrimaryKey));
    }

    public override void Revert()
    {
        Database.RemoveTable("Prism_FileHost");
    }
}
