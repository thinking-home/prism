using Prism.Host;
using Prism.Host.Media;

var builder = WebApplication.CreateBuilder(args);

// ---- Конфигурация --------------------------------------------------------
// Читаем настройки Player из appsettings.json (секция "Player"), затем
// разрешаем переопределить папку с медиа / кодек прямо из командной строки:
//   dotnet run -- --file <путь>           (раздать папку конкретного файла)
//   dotnet run -- --media <папка> --codec h265
var options = new PlayerOptions();
builder.Configuration.GetSection("Player").Bind(options);
ApplyCommandLine(args, options);

// Относительную папку с медиа разрешаем относительно корня приложения, чтобы
// использовалась одна и та же папка "videos" независимо от рабочего каталога shell.
if (!Path.IsPathRooted(options.MediaDirectory))
    options.MediaDirectory = Path.Combine(builder.Environment.ContentRootPath, options.MediaDirectory);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<FFTools>();
builder.Services.AddSingleton<MediaProbe>();
builder.Services.AddSingleton<MediaLibrary>();
builder.Services.AddSingleton<HlsTranscoder>();

// Клиент (Prism.Client) живёт на другом origin (dev-сервер Vite), поэтому
// разрешаем кросс-доменные запросы к API. Для домашнего плеера это безопасно.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

var library = app.Services.GetRequiredService<MediaLibrary>();
var transcoder = app.Services.GetRequiredService<HlsTranscoder>();
var tools = app.Services.GetRequiredService<FFTools>();

// ---- Информация о сервере ------------------------------------------------
// Единственная настройка клиента — URL сервера; всё остальное он берёт отсюда.
app.MapGet("/api/info", () => Results.Json(new
{
    name = "Prism",
    ffmpegAvailable = tools.Available,
    outputCodec = options.OutputCodec,
    segmentSeconds = options.SegmentSeconds,
    audioBitrateKbps = options.AudioBitrateKbps,
    audioSampleRate = options.AudioSampleRate,
    mediaDirectory = library.MediaDirectory,
    mediaCount = library.Scan().Count,
}));

// ---- Список медиа-библиотеки ---------------------------------------------
app.MapGet("/api/media", async (CancellationToken ct) =>
{
    var items = library.Scan();
    var list = new List<object>();
    foreach (var it in items)
    {
        await library.ResolveAsync(it, ct);
        list.Add(MediaDto(it));
    }
    return Results.Json(list);
});

// ---- Одна запись медиа-библиотеки ----------------------------------------
app.MapGet("/api/media/{id}", async (string id, CancellationToken ct) =>
{
    library.Scan();
    var item = library.Get(id);
    if (item is null) return Results.NotFound();
    await library.ResolveAsync(item, ct);
    return Results.Json(MediaDto(item));
});

// ---- HLS-плейлист --------------------------------------------------------
app.MapGet("/hls/{id}/playlist.m3u8", async (string id, CancellationToken ct) =>
{
    library.Scan();
    var item = library.Get(id);
    if (item is null) return Results.NotFound();
    await library.ResolveAsync(item, ct);
    if (item.Mode != PlaybackMode.Transcode || item.Info is null)
        return Results.BadRequest("Этот файл не раздаётся через HLS.");

    var playlist = transcoder.BuildPlaylist(item.Info);
    return Results.Text(playlist, "application/vnd.apple.mpegurl");
});

// ---- HLS-сегмент (транскодирование на лету) ------------------------------
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
        await transcoder.WriteSegmentAsync(item, index, http.Response.Body, ct);
    }
    catch (OperationCanceledException)
    {
        // Клиент ушёл со страницы / перемотал — делать больше нечего.
    }
});

// ---- Прямой стриминг (HTTP Range) для браузерных файлов ------------------
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

// ---- Стартовый баннер ----------------------------------------------------
var initial = library.Scan();
app.Logger.LogInformation("Папка с медиа     : {dir}", library.MediaDirectory);
app.Logger.LogInformation("Найдено файлов    : {count}", initial.Count);
app.Logger.LogInformation("Выходной кодек    : {codec}", options.OutputCodec);
app.Logger.LogInformation("ffmpeg доступен   : {avail}", tools.Available);

app.Run();
return;

// --------------------------------------------------------------------------
// DTO записи медиа-библиотеки для клиента. streamUrl — относительный путь,
// клиент дополняет его базовым URL сервера.
static object MediaDto(MediaItem it) => new
{
    id = it.Id,
    title = it.DisplayName,
    fileName = it.FileName,
    streamType = it.Mode switch
    {
        PlaybackMode.Transcode => "hls",
        PlaybackMode.Direct => "direct",
        _ => "unsupported",
    },
    playable = it.Mode != PlaybackMode.Unsupported,
    streamUrl = it.Mode switch
    {
        PlaybackMode.Transcode => $"/hls/{it.Id}/playlist.m3u8",
        PlaybackMode.Direct => $"/raw/{it.Id}",
        _ => null,
    },
    durationSeconds = it.Info?.DurationSeconds ?? 0,
    width = it.Info?.Width ?? 0,
    height = it.Info?.Height ?? 0,
    videoCodec = it.Info?.VideoCodec,
    audioCodec = it.Info?.AudioCodec,
    audioChannels = it.Info?.AudioChannels ?? 0,
    statusMessage = it.StatusMessage,
};

static void ApplyCommandLine(string[] args, PlayerOptions options)
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--file" when i + 1 < args.Length:
                // Раздать папку, в которой лежит этот файл.
                var file = args[++i];
                var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                if (!string.IsNullOrEmpty(dir)) options.MediaDirectory = dir;
                break;
            case "--media" or "--dir" when i + 1 < args.Length:
                options.MediaDirectory = args[++i];
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
}
