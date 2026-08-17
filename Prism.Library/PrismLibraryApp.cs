using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Prism.Abstractions;
using Serilog;
using ThinkingHome.Migrator;

namespace Prism.Library;

/// <summary>
/// Сборка и запуск сервиса библиотеки — общая точка входа для всех способов
/// запуска (Prism.Library.Console, позже Prism.Library.Service). Сервис хранит
/// дерево групп, мету и правила автозаполнения; файлы получает с хостов Prism
/// (список — в конфиге), клиентам отдаёт агрегированный каталог и API плееров.
/// </summary>
public static class PrismLibraryApp
{
    public static void Run(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        configure?.Invoke(builder);

        // ---- Логи: та же схема, что у хоста (консоль + файлы с ротацией) -------
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(builder.Environment.ContentRootPath, "logs", "prism-library-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 32 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14)
            .CreateLogger();
        builder.Host.UseSerilog();

        // ---- БД: провайдер и строка — из конфига, схему ведёт мигратор ---------
        // (ключ версионирования prism.library — тот же, что был у плагина, поэтому
        // существующая БД подхватывается без повторного прогона миграций).
        var provider = builder.Configuration["Database:Provider"] ?? "sqlite";
        var connectionString = ResolveConnectionString(
            provider, builder.Configuration, builder.Environment.ContentRootPath);
        using (var migrator = new Migrator(provider, connectionString, typeof(PrismLibraryApp).Assembly))
            migrator.Migrate(-1);

        // Единственное место, знающее про конкретный провайдер EF.
        builder.Services.AddDbContextFactory<LibraryDbContext>(o =>
        {
            if (provider == "postgres") o.UseNpgsql(connectionString);
            else o.UseSqlite(connectionString);
        });

        // Идентичность файлов — HTTP-опрос хостов Prism из конфига (секция "Hosts").
        var hosts = builder.Configuration.GetSection("Hosts").Get<HostEntry[]>() ?? [];
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IReadOnlyList<HostEntry>>(hosts);
        builder.Services.AddSingleton<IMediaIdentity, HttpMediaIdentity>();

        // Фоновое обслуживание (ремап + правила) — как в плагине: синглтон +
        // hosted-обёртка, тот же экземпляр дёргает ручка /api/library/scan.
        builder.Services.AddSingleton<LibraryMaintenanceService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryMaintenanceService>());

        // Веб-клиент в dev-режиме живёт на другом origin (Vite) — разрешаем CORS,
        // как у хоста. Для домашней библиотеки это безопасно.
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();
        app.MapLibraryEndpoints();

        app.Logger.LogInformation("БД                : {provider}", provider);
        app.Logger.LogInformation("Хосты             : {hosts}",
            hosts.Length == 0 ? "(не настроены)" : string.Join("; ", hosts.Select(h => $"{h.Name} = {h.BaseUrl}")));

        try
        {
            app.Run();
        }
        finally
        {
            Log.CloseAndFlush(); // дописать хвост файла при остановке
        }
    }

    // Для SQLite относительный путь к файлу разрешаем относительно корня приложения
    // и создаём папку. Для Postgres строку берём как есть.
    private static string ResolveConnectionString(string provider, IConfiguration config, string contentRoot)
    {
        var configured = config["Database:ConnectionString"];
        if (provider != "sqlite")
            return configured ?? throw new InvalidOperationException("Не задана Database:ConnectionString.");

        var csb = new SqliteConnectionStringBuilder(configured ?? "Data Source=data/prism.db");
        if (!Path.IsPathRooted(csb.DataSource))
            csb.DataSource = Path.Combine(contentRoot, csb.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(csb.DataSource)!);
        return csb.ConnectionString;
    }
}
