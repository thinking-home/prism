namespace Prism.Library;

/// <summary>Значения EntityType в таблице меты. (В плагине жил рядом с
/// IMediaMetaSource; в сервисе подмешивание меты появится вместе с агрегированным
/// каталогом — шаг 3, — а константы нужны эндпоинтам и обслуживанию уже сейчас.)</summary>
public static class MetaEntity
{
    public const string File = "file";
    public const string Node = "node";
}

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

/// <summary>Какой хост видел файл (бухгалтерия «id → хост»): по ней преемник
/// осиротевшего id спрашивается только у хостов, знавших файл, а не у всех.
/// Host — имя хоста из секции Hosts конфига (при переименовании хоста старые
/// строки перестают находиться — сработает фолбэк опроса всех). Строки
/// переживают исчезновение файла — это и есть память для ремапа; чистятся
/// вместе с записями файла в gc.</summary>
public class FileHostRecord
{
    public string FileKey { get; set; } = "";
    public string Host { get; set; } = "";
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
