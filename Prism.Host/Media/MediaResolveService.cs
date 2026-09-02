using System.Diagnostics;

namespace Prism.Host.Media;

/// <summary>
/// Фоновая доразборка: находит файлы без метаданных (ffprobe ещё не выполнялся)
/// и разбирает их, не блокируя запросы, — /api/media отдаёт такие файлы сразу в
/// переходном состоянии (streamType "pending"), а после доразборки — полными.
/// Просыпается по сигналу скана (обнаружен файл без разбора), при старте и по
/// таймеру. Результаты сохраняются в персистентный кэш пакетно — раз в
/// несколько секунд и в конце прохода, а не после каждого файла (иначе холодное
/// заполнение большой библиотеки переписывало бы файл кэша квадратичным объёмом).
/// </summary>
public sealed class MediaResolveService(MediaLibrary library, MediaInfoCache infoCache,
    ILogger<MediaResolveService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    // Сигнал «пройтись сейчас» от скана; ёмкость 1 — повторные сигналы во время
    // прохода схлопываются в один дополнительный проход.
    private readonly SemaphoreSlim _wake = new(0, 1);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        library.PendingDiscovered += Wake;
        return base.StartAsync(cancellationToken);
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* проход уже запрошен */ }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pending = library.Scan().Where(i => i.Info is null).ToList();
                if (pending.Count > 0)
                {
                    logger.LogInformation("Фоновая доразборка: файлов без метаданных {count}", pending.Count);
                    var flush = Stopwatch.StartNew();
                    foreach (var item in pending)
                    {
                        ct.ThrowIfCancellationRequested();
                        await library.ResolveAsync(item, ct);
                        if (flush.Elapsed >= FlushInterval)
                        {
                            infoCache.SaveIfDirty();
                            flush.Restart();
                        }
                    }
                    logger.LogInformation("Фоновая доразборка завершена: {count}", pending.Count);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка фоновой доразборки");
            }
            finally
            {
                infoCache.SaveIfDirty();
            }

            // Ждём таймер или сигнал скана — что случится раньше.
            try { await _wake.WaitAsync(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
