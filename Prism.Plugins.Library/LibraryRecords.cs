namespace Prism.Plugins.Library;

/// <summary>Группа виртуального дерева библиотеки; вложенность — через ParentId.
/// Id — GUID, генерируется кодом при создании (без обращения к БД), поэтому
/// связанные записи можно сохранять одной операцией.</summary>
public class LibraryNode
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>Членство файла в группе. FileKey — ключ содержимого (fingerprint),
/// поэтому связь переживает переносы файлов; файл может быть в нескольких группах.</summary>
public class NodeItemRecord
{
    public Guid NodeId { get; set; }
    public string FileKey { get; set; } = "";
}

/// <summary>Мета: свободные пары ключ-значение. EntityType — "file" (EntityKey =
/// fingerprint) или "node" (EntityKey = id группы). Сервер семантику не знает —
/// потребитель работает с теми ключами, о которых знает сам.</summary>
public class MetaRecord
{
    public string EntityType { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
