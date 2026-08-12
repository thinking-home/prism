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
    /// <summary>Шаблон относительного пути с плейсхолдерами <c>{имя}</c> и
    /// звёздочками <c>*</c>, напр. <c>*/{series}.s{season}e{episode}*</c>.
    /// Сопоставление — с путём целиком, без расширения файла и без учёта
    /// регистра; <c>{имя}</c> захватывает непустой кусок одного сегмента пути,
    /// <c>*</c> — любой, в том числе пустой, и никуда не подставляется.</summary>
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

    // Разбор шаблона: плейсхолдер {имя} или звёздочка. Звёздочка — «любые
    // символы сегмента, в том числе ни одного»; нужна для реальных имён, где
    // до/после значимого куска стоит произвольный мусор («…1080p.WEB-DL.RG»),
    // которого может и не быть вовсе.
    private static readonly Regex TemplateToken = new(@"\{(\w+)\}|\*");

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
    // группа «непустой кусок без /», * → то же самое, но без захвата и с
    // допустимым пустым совпадением. Ленивость + якоря дают ожидаемый разбор
    // «{title}s{season}e{episode}», а запрет '/' не пускает плейсхолдер и
    // звёздочку через границу папки. Регистр не учитывается (s01e02 == S01E02).
    //
    // Особый случай — плейсхолдер прямо перед звёздочкой: две ленивые группы
    // подряд делят строку по минимуму, и {episode}* дал бы «0» вместо «01»
    // (остаток забрала бы звёздочка). Поэтому там плейсхолдер жадно берёт
    // подряд идущие буквы и цифры — до первого разделителя (точки, пробела,
    // скобки), с которого и начинается «мусор» реальных имён.
    private static Regex Compile(string template)
    {
        var normalized = template.Replace('\\', '/').TrimStart('/');
        var sb = new StringBuilder("^");
        var pos = 0;
        var tokens = TemplateToken.Matches(normalized);
        for (var i = 0; i < tokens.Count; i++)
        {
            var m = tokens[i];
            sb.Append(Regex.Escape(normalized[pos..m.Index]));
            if (m.Groups[1].Success)
            {
                var beforeStar = i + 1 < tokens.Count
                    && !tokens[i + 1].Groups[1].Success
                    && tokens[i + 1].Index == m.Index + m.Length;
                sb.Append("(?<").Append(m.Groups[1].Value).Append('>')
                  .Append(beforeStar ? "[^\\W_]+" : "[^/]+?").Append(')');
            }
            else
            {
                sb.Append("[^/]*?");
            }
            pos = m.Index + m.Length;
        }
        sb.Append(Regex.Escape(normalized[pos..])).Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
