using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Registers the SQLite library database, cover-art cache and repository.</summary>
    public static IServiceCollection AddLibraryData(this IServiceCollection services)
    {
        services.AddDbContextFactory<LibraryDbContext>(options =>
            options.UseSqlite(AppPaths.SqliteConnectionString));

        services.AddSingleton<ICoverArtCache, CoverArtCache>();
        services.AddSingleton<ILibraryRepository, LibraryRepository>();
        services.AddSingleton<IMediaSourceManager, MediaSourceManager>();
        services.AddSingleton<ILibrarySyncService, LibrarySyncService>();
        services.TryAddSingleton<IAppSettings, AppSettingsStore>();

        return services;
    }

    /// <summary>Applies any pending EF Core migrations, creating the database on first run.</summary>
    public static async Task MigrateLibraryAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<LibraryDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
