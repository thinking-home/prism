using Prism.Abstractions;
using Prism.Host.Media;
using Serilog;

namespace Prism.Host;

/// <summary>
/// Сборка и запуск сервера Prism — общая точка входа для всех способов запуска.
/// Исполняемые проекты (Prism.Host.Console, Prism.Host.Service) готовят
/// <see cref="WebApplicationOptions"/> под свой сценарий и передают сюда.
/// </summary>
public static class PrismHostApp
{
    public static void Run(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var args = options.Args ?? [];
        var builder = WebApplication.CreateBuilder(options);
        configure?.Invoke(builder);

        // ---- Логи ------------------------------------------------------------
        // Serilog: консоль (как раньше) + файлы с ротацией в logs/ рядом с
        // приложением (у службы Windows content root — папка установки, поэтому
        // путь абсолютный). Уровни — секция "Serilog" в appsettings.json.
        // Логгер создаётся сразу (а не в колбэке UseSerilog), чтобы в файл попадали
        // и сообщения этапа сборки приложения — например, загрузки плагинов.
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(builder.Environment.ContentRootPath, "logs", "prism-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 32 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14)
            .CreateLogger();
        builder.Host.UseSerilog();

        // ---- Конфигурация ----------------------------------------------------
        // Читаем настройки Player из appsettings.json (секция "Player"), затем
        // разрешаем переопределить папку с медиа / кодек прямо из командной строки:
        //   dotnet run -- --file <путь>           (раздать папку конкретного файла)
        //   dotnet run -- --media <папка> --codec h265
        var player = new PlayerOptions();
        builder.Configuration.GetSection("Player").Bind(player);
        ApplyCommandLine(args, player);

        if (player.MediaDirectories.Length == 0)
            player.MediaDirectories = [PlayerOptions.DefaultMediaDirectory];

        // Относительные папки с медиа разрешаем относительно корня приложения, чтобы
        // использовалась одна и та же папка "videos" независимо от рабочего каталога shell.
        player.MediaDirectories = player.MediaDirectories
            .Select(d => Path.IsPathRooted(d) ? d : Path.Combine(builder.Environment.ContentRootPath, d))
            .ToArray();

        builder.Services.AddSingleton(player);
        builder.Services.AddSingleton<FFTools>();
        builder.Services.AddSingleton<MediaProbe>();
        builder.Services.AddSingleton<MediaLibrary>();
        builder.Services.AddSingleton<IMediaIdentity, MediaIdentity>();
        builder.Services.AddSingleton<HlsTranscoder>();
        builder.Services.AddSingleton<SubtitleService>();

        // Плагины: список сборок из appsettings ("Plugins"). Без плагинов ядро работает
        // как обычно. Каждый модуль регистрирует свои сервисы и (ниже) эндпоинты.
        // Отдаём плагинам корень приложения (для разрешения относительных путей, напр. SQLite).
        builder.Configuration["ContentRoot"] = builder.Environment.ContentRootPath;
        var modules = PluginLoader.Load(builder.Configuration, builder.Environment.ContentRootPath,
            LoggerFactory.Create(b => b.AddSerilog()).CreateLogger("Plugins"));
        foreach (var module in modules)
            module.ConfigureServices(builder.Services, builder.Configuration);

        // Клиент (Prism.Client) живёт на другом origin (dev-сервер Vite), поэтому
        // разрешаем кросс-доменные запросы к API. Для домашнего плеера это безопасно.
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();

        // Эндпоинты плагинов.
        foreach (var module in modules)
            module.MapEndpoints(app);

        var metaSources = app.Services.GetServices<IMediaMetaSource>().ToArray();

        var library = app.Services.GetRequiredService<MediaLibrary>();
        var transcoder = app.Services.GetRequiredService<HlsTranscoder>();
        var subtitles = app.Services.GetRequiredService<SubtitleService>();
        var tools = app.Services.GetRequiredService<FFTools>();

        // ---- Информация о сервере ----------------------------------------------
        // Единственная настройка клиента — URL сервера; всё остальное он берёт отсюда.
        app.MapGet("/api/info", () => Results.Json(new
        {
            name = "Prism",
            ffmpegAvailable = tools.Available,
            outputCodec = player.OutputCodec,
            segmentSeconds = player.SegmentSeconds,
            audioBitrateKbps = player.AudioBitrateKbps,
            audioSampleRate = player.AudioSampleRate,
            mediaDirectories = library.MediaDirectories,
            mediaCount = library.Scan().Count,
        }));

        // ---- Список медиа-библиотеки -------------------------------------------
        app.MapGet("/api/media", async (CancellationToken ct) =>
        {
            var items = library.Scan();
            var byId = new Dictionary<string, Dictionary<string, object?>>();
            var list = new List<Dictionary<string, object?>>();
            foreach (var it in items)
            {
                await library.ResolveAsync(it, ct);
                var dto = MediaDto(it);
                list.Add(dto);
                byId[it.Id] = dto;
            }
            await ApplyMetaAsync(metaSources, byId, ct);
            return Results.Json(list);
        });

        // ---- Одна запись медиа-библиотеки --------------------------------------
        app.MapGet("/api/media/{id}", async (string id, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            var dto = MediaDto(item);
            await ApplyMetaAsync(metaSources, new() { [id] = dto }, ct);
            return Results.Json(dto);
        });

        // ---- Резолв файла по отпечатку содержимого (для лаунчера) --------------
        // Лаунчер считает отпечаток кликнутого файла (Prism.Common: размер + хеш
        // краёв) и спрашивает, есть ли такой файл в библиотеке. Id файла и есть
        // отпечаток, поэтому резолв — прямой поиск по каталогу. Ответ — та же
        // запись, что и в /api/media; 404 — файла в библиотеке нет.
        app.MapGet("/api/resolve", async (long size, string fingerprint, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get($"{size}-{fingerprint}");
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            return Results.Json(MediaDto(item));
        });

        // ---- HLS: master-плейлист (вариант видео + рендиции аудио/субтитров) ---
        app.MapGet("/hls/{id}/playlist.m3u8", async (string id, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            if (item.Mode != PlaybackMode.Transcode || item.Info is null)
                return Results.BadRequest("Этот файл не раздаётся через HLS.");

            return Results.Text(transcoder.BuildMasterPlaylist(item.Info), "application/vnd.apple.mpegurl");
        });

        // ---- HLS: медиа-плейлист видео ------------------------------------------
        app.MapGet("/hls/{id}/video.m3u8", async (string id, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            if (item.Mode != PlaybackMode.Transcode || item.Info is null)
                return Results.BadRequest("Этот файл не раздаётся через HLS.");

            return Results.Text(transcoder.BuildVideoPlaylist(item.Info), "application/vnd.apple.mpegurl");
        });

        // ---- HLS: медиа-плейлист аудиодорожки -----------------------------------
        app.MapGet("/hls/{id}/audio/{track:int}.m3u8", async (string id, int track, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            if (item.Mode != PlaybackMode.Transcode || item.Info is null)
                return Results.BadRequest("Этот файл не раздаётся через HLS.");
            if (track < 0 || track >= item.Info.AudioTracks.Count) return Results.NotFound();

            return Results.Text(transcoder.BuildAudioPlaylist(item.Info, track), "application/vnd.apple.mpegurl");
        });

        // ---- HLS: плейлист дорожки субтитров (обёртка над WebVTT) ---------------
        app.MapGet("/hls/{id}/subs/{track:int}.m3u8", async (string id, int track, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            if (item.Mode != PlaybackMode.Transcode || item.Info is null)
                return Results.BadRequest("Этот файл не раздаётся через HLS.");
            if (track < 0 || track >= item.Info.SubtitleTracks.Count ||
                !item.Info.SubtitleTracks[track].TextBased)
                return Results.NotFound();

            return Results.Text(transcoder.BuildSubtitlePlaylist(item.Info, track), "application/vnd.apple.mpegurl");
        });

        // ---- HLS: WebVTT-сегмент дорожки субтитров ------------------------------
        app.MapGet("/hls/{id}/subs/{track:int}/{index:int}.vtt", async (string id, int track, int index, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            if (item.Mode != PlaybackMode.Transcode || item.Info is null)
                return Results.BadRequest("Этот файл не раздаётся через HLS.");
            if (track < 0 || track >= item.Info.SubtitleTracks.Count ||
                !item.Info.SubtitleTracks[track].TextBased ||
                index < 0 || index >= transcoder.SegmentCount(item.Info))
                return Results.NotFound();

            var vtt = await subtitles.GetVttSegmentAsync(item, track, transcoder.SegmentLengthSeconds, index);
            return vtt is null
                ? Results.NotFound("Субтитры недоступны (не текстовые или ошибка извлечения).")
                : Results.Text(vtt, "text/vtt; charset=utf-8");
        });

        // ---- HLS: видеосегмент (транскодирование на лету) -----------------------
        app.MapGet("/hls/{id}/segment/{index:int}.ts", async (string id, int index, HttpContext http, CancellationToken ct) =>
        {
            var item = library.Get(id);
            if (item is null) { http.Response.StatusCode = 404; return; }
            await library.ResolveAsync(item, ct);
            if (item.Mode != PlaybackMode.Transcode || item.Info is null)
            {
                http.Response.StatusCode = 400;
                return;
            }
            if (index < 0 || index >= transcoder.SegmentCount(item.Info))
            {
                http.Response.StatusCode = 404;
                return;
            }

            http.Response.ContentType = "video/mp2t";
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                await transcoder.WriteVideoSegmentAsync(item, index, http.Response.Body, ct);
            }
            catch (OperationCanceledException)
            {
                // Клиент ушёл со страницы / перемотал — делать больше нечего.
            }
        });

        // ---- HLS: аудиосегмент (лёгкая сессия только-аудио) ---------------------
        app.MapGet("/hls/{id}/audio/{track:int}/{index:int}.ts", async (string id, int track, int index, HttpContext http, CancellationToken ct) =>
        {
            var item = library.Get(id);
            if (item is null) { http.Response.StatusCode = 404; return; }
            await library.ResolveAsync(item, ct);
            if (item.Mode != PlaybackMode.Transcode || item.Info is null)
            {
                http.Response.StatusCode = 400;
                return;
            }
            if (track < 0 || track >= item.Info.AudioTracks.Count ||
                index < 0 || index >= transcoder.SegmentCount(item.Info))
            {
                http.Response.StatusCode = 404;
                return;
            }

            http.Response.ContentType = "video/mp2t";
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                await transcoder.WriteAudioSegmentAsync(item, index, track, http.Response.Body, ct);
            }
            catch (OperationCanceledException)
            {
                // Клиент ушёл со страницы / перемотал — делать больше нечего.
            }
        });

        // ---- Дебаг: активные сессии транскодирования и метрики процессов -------
        app.MapGet("/api/debug/sessions", () => Results.Json(new
        {
            serverCpuSeconds = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds,
            cpuCount = Environment.ProcessorCount,
            sessions = transcoder.DebugSnapshot(),
        }));

        // ---- Субтитры в WebVTT (текстовые дорожки) ------------------------------
        app.MapGet("/api/media/{id}/subtitle/{index:int}.vtt", async (string id, int index, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);

            var path = await subtitles.GetVttPathAsync(item, index);
            return path is null
                ? Results.NotFound("Субтитры недоступны (не текстовые или ошибка извлечения).")
                : Results.File(path, "text/vtt; charset=utf-8");
        });

        // ---- Прямой стриминг (HTTP Range) для браузерных файлов ----------------
        app.MapGet("/raw/{id}", async (string id, CancellationToken ct) =>
        {
            library.Scan();
            var item = library.Get(id);
            if (item is null) return Results.NotFound();
            await library.ResolveAsync(item, ct);
            if (!File.Exists(item.Path)) return Results.NotFound();

            var contentType = Path.GetExtension(item.Path).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" or ".mov" => "video/mp4",
                ".webm" => "video/webm",
                _ => "application/octet-stream",
            };
            return Results.File(item.Path, contentType, enableRangeProcessing: true);
        });

        // ---- Стартовый баннер ---------------------------------------------------
        var initial = library.Scan();
        app.Logger.LogInformation("Папки с медиа     : {dirs}", string.Join("; ", library.MediaDirectories));
        app.Logger.LogInformation("Найдено файлов    : {count}", initial.Count);
        app.Logger.LogInformation("Выходной кодек    : {codec}", player.OutputCodec);
        app.Logger.LogInformation("ffmpeg доступен   : {avail}", tools.Available);

        try
        {
            app.Run();
        }
        finally
        {
            Log.CloseAndFlush(); // дописать хвост файла при остановке
        }
    }

    // --------------------------------------------------------------------------
    // DTO записи медиа-библиотеки для клиента. streamUrl — относительный путь,
    // клиент дополняет его базовым URL сервера.
    // Базовые поля записи (ядро). Плагины через IMediaMetaSource могут добавить/
    // переопределить произвольные ключи (напр. "title" из метаданных библиотеки).
    private static Dictionary<string, object?> MediaDto(MediaItem it) => new()
    {
        ["id"] = it.Id,
        ["title"] = it.DisplayName,
        ["fileName"] = it.FileName,
        ["streamType"] = it.Mode switch
        {
            PlaybackMode.Transcode => "hls",
            PlaybackMode.Direct => "direct",
            _ => "unsupported",
        },
        ["playable"] = it.Mode != PlaybackMode.Unsupported,
        ["streamUrl"] = it.Mode switch
        {
            PlaybackMode.Transcode => $"/hls/{it.Id}/playlist.m3u8",
            PlaybackMode.Direct => $"/raw/{it.Id}",
            _ => null,
        },
        ["durationSeconds"] = it.Info?.DurationSeconds ?? 0,
        ["width"] = it.Info?.Width ?? 0,
        ["height"] = it.Info?.Height ?? 0,
        ["videoCodec"] = it.Info?.VideoCodec,
        ["audioCodec"] = it.Info?.AudioCodec,
        ["audioChannels"] = it.Info?.AudioChannels ?? 0,
        ["audioTracks"] = (it.Info?.AudioTracks ?? []).Select(x => new
        {
            index = x.Index, codec = x.Codec, language = x.Language, title = x.Title, channels = x.Channels,
        }),
        ["subtitleTracks"] = (it.Info?.SubtitleTracks ?? []).Select(x => new
        {
            index = x.Index, codec = x.Codec, language = x.Language, title = x.Title, textBased = x.TextBased,
        }),
        ["statusMessage"] = it.StatusMessage,
    };

    // Подмешивает в записи доп. поля от зарегистрированных источников метаданных.
    // Без источников — no-op (поведение ядра не меняется).
    private static async Task ApplyMetaAsync(IReadOnlyList<IMediaMetaSource> sources,
        Dictionary<string, Dictionary<string, object?>> byId, CancellationToken ct)
    {
        if (sources.Count == 0 || byId.Count == 0) return;
        var ids = byId.Keys.ToArray();
        foreach (var src in sources)
        {
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> meta;
            try { meta = await src.GetMetaAsync(ids, ct); }
            catch { continue; } // плагин не должен ломать выдачу библиотеки
            foreach (var (id, fields) in meta)
                if (byId.TryGetValue(id, out var dto))
                    foreach (var (k, v) in fields) dto[k] = v;
        }
    }

    private static void ApplyCommandLine(string[] args, PlayerOptions options)
    {
        // --media/--file можно указывать несколько раз; каждая папка ДОБАВЛЯЕТСЯ
        // к списку из конфига (не заменяет его) — так у службы Windows работают
        // одновременно --media от инсталлятора и папки, дописанные в appsettings.
        var dirs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file" when i + 1 < args.Length:
                    // Раздать папку, в которой лежит этот файл.
                    var file = args[++i];
                    var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                    if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
                    break;
                case "--media" or "--dir" when i + 1 < args.Length:
                    dirs.Add(args[++i]);
                    break;
                case "--codec" when i + 1 < args.Length:
                    options.OutputCodec = args[++i];
                    break;
                case "--ffmpeg" when i + 1 < args.Length:
                    options.FfmpegPath = args[++i];
                    break;
                case "--ffprobe" when i + 1 < args.Length:
                    options.FfprobePath = args[++i];
                    break;
            }
        }

        if (dirs.Count > 0) options.MediaDirectories = [.. options.MediaDirectories, .. dirs];
    }
}
