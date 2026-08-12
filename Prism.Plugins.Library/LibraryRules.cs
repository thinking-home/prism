using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Prism.Plugins.Library;

/// <summary>
/// Правило автозаполнения из конфига (секция "Library:Rules"). Шаблон пути
/// сопоставляется с относительным путём файла (без расширения); захваченные
/// плейсхолдеры подставляются в путь группы и значения меты.
/// </summary>
public sealed class LibraryRule
{
    /// <summary>Шаблон относительного пути с плейсхолдерами <c>{имя}</c>,
    /// напр. <c>series/{name}/{title} s{season}e{episode}</c>. Сопоставление —
    /// без расширения файла и без учёта регистра; плейсхолдер матчит кусок
    /// одного сегмента пути (не пересекает <c>/</c>).</summary>
    public string Path { get; set; } = "";

    /// <summary>Путь к группе через <c>/</c> (напр. <c>Сериалы/{name}</c>);
    /// недостающие группы создаются. Пусто — правило группу не назначает.</summary>
    public string? Node { get; set; }

    /// <summary>Мета-ключи: имя → шаблон значения (напр. <c>title</c> →
    /// <c>{title}</c>). Пусто — правило мету не назначает.</summary>
    public Dictionary<string, string> Meta { get; set; } = [];
}

/// <summary>Скомпилированное правило: шаблон пути превращён в regex.</summary>
public sealed class CompiledRule
{
    private static readonly Regex Placeholder = new(@"\{(\w+)\}");

    private readonly Regex _regex;

    public LibraryRule Rule { get; }

    private CompiledRule(LibraryRule rule, Regex regex)
    {
        Rule = rule;
        _regex = regex;
    }

    /// <summary>Читает правила из конфига и компилирует; правило без шаблона,
    /// без действий или с некорректным плейсхолдером пропускается с warning.</summary>
    public static IReadOnlyList<CompiledRule> Load(IConfiguration config, ILogger logger)
    {
        var rules = config.GetSection("Library:Rules").Get<LibraryRule[]>() ?? [];
        var result = new List<CompiledRule>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Path) ||
                (string.IsNullOrWhiteSpace(rule.Node) && rule.Meta.Count == 0))
            {
                logger.LogWarning("Правило автозаполнения без шаблона пути или без действий пропущено: '{path}'", rule.Path);
                continue;
            }
            try
            {
                result.Add(new CompiledRule(rule, Compile(rule.Path)));
            }
            catch (ArgumentException ex)
            {
                // Например, плейсхолдер, начинающийся с цифры, — недопустимое имя группы regex.
                logger.LogWarning(ex, "Правило автозаполнения '{path}' не удалось разобрать — пропущено", rule.Path);
            }
        }
        return result;
    }

    /// <summary>Сопоставляет правило с относительным путём (уже без расширения).
    /// null — не совпало; иначе — значения плейсхолдеров, обрезанные от краевых
    /// пробелов (чтобы «Патриот s01e02» не давал title с хвостовым пробелом).</summary>
    public Dictionary<string, string>? Match(string relativePathNoExt)
    {
        var m = _regex.Match(relativePathNoExt);
        if (!m.Success) return null;

        var values = new Dictionary<string, string>();
        foreach (var name in _regex.GetGroupNames())
            if (!int.TryParse(name, out _))
                values[name] = m.Groups[name].Value.Trim();
        return values;
    }

    /// <summary>Подставляет значения плейсхолдеров в шаблон действия (путь группы,
    /// значение меты). Неизвестный плейсхолдер остаётся литералом — опечатка в
    /// правиле сразу видна в результате.</summary>
    public static string Substitute(string template, Dictionary<string, string> values) =>
        Placeholder.Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    // Шаблон пути → regex: литералы экранируются, {имя} → ленивая именованная
    // группа «любые символы, кроме /». Ленивость + якоря дают ожидаемый разбор
    // «{title}s{season}e{episode}», а запрет '/' не пускает плейсхолдер через
    // границу папки. Регистр не учитывается (s01e02 == S01E02).
    private static Regex Compile(string template)
    {
        var normalized = template.Replace('\\', '/').TrimStart('/');
        var sb = new StringBuilder("^");
        var pos = 0;
        foreach (Match m in Placeholder.Matches(normalized))
        {
            sb.Append(Regex.Escape(normalized[pos..m.Index]));
            sb.Append("(?<").Append(m.Groups[1].Value).Append(">[^/]+?)");
            pos = m.Index + m.Length;
        }
        sb.Append(Regex.Escape(normalized[pos..])).Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
