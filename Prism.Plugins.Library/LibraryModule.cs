using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prism.Abstractions;
using ThinkingHome.Migrator;

namespace Prism.Plugins.Library;

/// <summary>
/// Плагин библиотеки метаданных. Провайдер и строка подключения берутся из конфига
/// (секция "Database"); код про конкретную СУБД не знает. Схему создаёт мигратор.
/// </summary>
public sealed class LibraryModule : IPrismModule
{
    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        var provider = config["Database:Provider"] ?? "sqlite";
        var connectionString = ResolveConnectionString(provider, config);

        // Схему ведёт мигратор (ключ prism.library) — независимо от СУБД.
        using (var migrator = new Migrator(provider, connectionString, typeof(LibraryModule).Assembly))
            migrator.Migrate(-1);

        // Единственное место, знающее про конкретный провайдер EF.
        services.AddDbContextFactory<LibraryDbContext>(o =>
        {
            if (provider == "postgres") o.UseNpgsql(connectionString);
            else o.UseSqlite(connectionString);
        });

        services.AddSingleton<IMediaMetaSource, MetadataMetaSource>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPut("/api/media/{id}/metadata",
            async (string id, MediaMetadataInput input, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Metadata.FindAsync([id], ct) ?? new MediaMetadataRecord { MediaId = id };
            row.Kind = input.Kind ?? "movie";
            row.Title = input.Title;
            row.SeriesTitle = input.SeriesTitle;
            row.Season = input.Season;
            row.Episode = input.Episode;
            if (db.Entry(row).State == EntityState.Detached) db.Metadata.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapDelete("/api/media/{id}/metadata",
            async (string id, IDbContextFactory<LibraryDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Metadata.FindAsync([id], ct);
            if (row is null) return Results.NotFound();
            db.Metadata.Remove(row);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // Для SQLite относительный путь к файлу разрешаем относительно корня приложения
    // (ContentRoot передаёт хост) и создаём папку. Для Postgres строку берём как есть.
    private static string ResolveConnectionString(string provider, IConfiguration config)
    {
        var configured = config["Database:ConnectionString"];
        if (provider != "sqlite")
            return configured ?? throw new InvalidOperationException("Не задана Database:ConnectionString.");

        var contentRoot = config["ContentRoot"] ?? Directory.GetCurrentDirectory();
        var csb = new SqliteConnectionStringBuilder(configured ?? "Data Source=data/prism.db");
        if (!Path.IsPathRooted(csb.DataSource))
            csb.DataSource = Path.Combine(contentRoot, csb.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(csb.DataSource)!);
        return csb.ConnectionString;
    }
}

/// <summary>Входная модель для установки метаданных.</summary>
public sealed record MediaMetadataInput
{
    public string? Kind { get; init; }
    public string? Title { get; init; }
    public string? SeriesTitle { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
}
