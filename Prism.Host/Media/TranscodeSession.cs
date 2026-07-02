using System.Diagnostics;

namespace Prism.Host.Media;

/// <summary>
/// Одна сессия транскодирования: процесс ffmpeg, который своим HLS-муксером пишет
/// ограниченный диапазон сегментов <c>[StartIndex, EndIndex)</c> в собственную
/// временную папку и затем завершается сам (по <c>-t</c>). Внутри сессии аудио —
/// единый поток, поэтому стыки сегментов бесшовные; разрыв возможен только на
/// границе между сессиями.
/// </summary>
internal sealed class TranscodeSession : IAsyncDisposable
{
    /// <summary>Индекс первого сегмента сессии.</summary>
    public int StartIndex { get; }

    /// <summary>Индекс за последним сегментом сессии (полуинтервал).</summary>
    public int EndIndex { get; }

    /// <summary>Индекс выбранной аудиодорожки (среди аудиопотоков файла).</summary>
    public int AudioTrack { get; }

    /// <summary>Временная папка с сегментами этой сессии.</summary>
    public string Directory { get; }

    /// <summary>Счётчик последнего обращения — для вытеснения по LRU.</summary>
    public long LastTouch { get; set; }

    /// <summary>Время последнего обращения (Environment.TickCount64) — для уборщика простоя.</summary>
    public long LastAccessMs { get; set; }

    private readonly Process _proc;
    private readonly object _gate = new();
    private volatile bool _disposed;

    public TranscodeSession(int startIndex, int endIndex, int audioTrack, string dir, Process proc)
    {
        StartIndex = startIndex;
        EndIndex = endIndex;
        AudioTrack = audioTrack;
        Directory = dir;
        _proc = proc;
        LastAccessMs = Environment.TickCount64;
    }

    /// <summary>PID процесса ffmpeg (или -1).</summary>
    public int Pid { get { try { return _disposed ? -1 : _proc.Id; } catch { return -1; } } }

    /// <summary>Занятая процессом память, байт (0 если недоступно).</summary>
    public long MemoryBytes
    {
        get { try { if (_disposed) return 0; _proc.Refresh(); return _proc.WorkingSet64; } catch { return 0; } }
    }

    /// <summary>Суммарное процессорное время процесса, сек (для расчёта %CPU по дельте).</summary>
    public double CpuSeconds
    {
        get { try { if (_disposed) return 0; _proc.Refresh(); return _proc.TotalProcessorTime.TotalSeconds; } catch { return 0; } }
    }

    public bool Covers(int index) => index >= StartIndex && index < EndIndex;

    public string SegmentPath(int index) => Path.Combine(Directory, $"seg{index}.ts");

    public bool SegmentReady(int index) => File.Exists(SegmentPath(index));

    // Безопасно после Dispose: освобождённый Process кидает исключение — трактуем
    // такой процесс как завершённый.
    public bool HasExited
    {
        get
        {
            if (_disposed) return true;
            try { return _proc.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>Максимальный индекс уже записанного сегмента (или StartIndex-1).</summary>
    public int HighestProduced()
    {
        var max = StartIndex - 1;
        try
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "seg*.ts"))
            {
                var name = Path.GetFileNameWithoutExtension(f); // "segN"
                if (name.Length > 3 && int.TryParse(name.AsSpan(3), out var n) && n > max)
                    max = n;
            }
        }
        catch
        {
            // Папку могли удалить параллельно — ничего страшного.
        }
        return max;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { await _proc.WaitForExitAsync(new CancellationTokenSource(3000).Token); } catch { }
        try { _proc.Dispose(); } catch { }
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
    }
}
