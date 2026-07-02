namespace Prism.Abstractions;

/// <summary>
/// Источник дополнительных полей для записей медиа-библиотеки. При формировании
/// ответа <c>/api/media</c> ядро спрашивает все зарегистрированные источники и
/// подмешивает к каждой записи произвольные пары ключ-значение. Без источников
/// ответ содержит только базовые поля ядра.
/// </summary>
public interface IMediaMetaSource
{
    /// <summary>
    /// Для указанных media-id вернуть доп. поля: <c>mediaId → (ключ → значение)</c>.
    /// Отсутствие id в результате означает «нет доп. полей». Значения сериализуются
    /// в JSON как есть.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>> GetMetaAsync(
        IReadOnlyCollection<string> mediaIds, CancellationToken ct = default);
}
