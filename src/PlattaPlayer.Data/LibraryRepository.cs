using Microsoft.EntityFrameworkCore;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Data;

/// <summary>EF Core implementation of <see cref="ILibraryRepository"/> backed by SQLite.</summary>
public sealed class LibraryRepository : ILibraryRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory;
    private readonly IAppSettings _settings;

    public LibraryRepository(IDbContextFactory<LibraryDbContext> factory, IAppSettings settings)
    {
        _factory = factory;
        _settings = settings;
    }

    /// <summary>
    /// Sources the user has filtered out of the library views (<see cref="IAppSettings.HiddenSourceIds"/>). Browse,
    /// count and Home queries leave out their tracks, and albums/artists left with none; playlists keep every entry
    /// so their position-based edits stay aligned.
    /// </summary>
    private string[] Hidden() => _settings.HiddenSourceIds.ToArray();

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking()
            .Include(a => a.AlbumArtist)
            .Where(a => a.Tracks.Any(t => !hidden.Contains(t.SourceId)))
            .OrderBy(a => a.SortTitle)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Artists.AsNoTracking()
            .Include(a => a.Albums.Where(al => al.Tracks.Any(t => !hidden.Contains(t.SourceId))))
            .Where(a => a.Albums.Any(al => al.Tracks.Any(t => !hidden.Contains(t.SourceId))))
            .OrderBy(a => a.SortName)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetAllTracksAsync(CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => !hidden.Contains(t.SourceId))
            .OrderBy(t => t.Title)
            .ToListAsync(ct);
    }

    public async Task<Album?> GetAlbumAsync(int albumId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking()
            .Include(a => a.AlbumArtist)
            .Include(a => a.Artists)
            .FirstOrDefaultAsync(a => a.Id == albumId, ct);
    }

    public async Task<Artist?> GetArtistAsync(int artistId, CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Artists.AsNoTracking()
            .Include(a => a.Albums.Where(al => al.Tracks.Any(t => !hidden.Contains(t.SourceId))))
            .FirstOrDefaultAsync(a => a.Id == artistId, ct);
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(int albumId, CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.AlbumId == albumId && !hidden.Contains(t.SourceId))
            .OrderBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.Title)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetArtistTracksAsync(int artistId, CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.Album!.Artists.Any(a => a.Id == artistId) && !hidden.Contains(t.SourceId))
            .OrderByDescending(t => t.Album!.Year).ThenBy(t => t.Album!.SortTitle)
            .ThenBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.Title)
            .ToListAsync(ct);
    }

    public async Task<LibraryCounts> GetLibraryCountsAsync(CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return new LibraryCounts(
            await db.Artists.CountAsync(a => a.Albums.Any(al => al.Tracks.Any(t => !hidden.Contains(t.SourceId))), ct),
            await db.Albums.CountAsync(a => a.Tracks.Any(t => !hidden.Contains(t.SourceId)), ct),
            await db.Tracks.CountAsync(t => !hidden.Contains(t.SourceId), ct));
    }

    public async Task<IReadOnlyDictionary<int, int>> GetTrackCountsByArtistAsync(CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Artists.AsNoTracking()
            .Select(a => new { ArtistId = a.Id, Count = a.Albums.SelectMany(al => al.Tracks).Count(t => !hidden.Contains(t.SourceId)) })
            .ToDictionaryAsync(x => x.ArtistId, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<GenreSummary>> GetGenresAsync(CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Group by the stored genre string per album in SQL (rows ≈ albums); split the "A; B" values here.
        var rows = await db.Tracks.AsNoTracking()
            .Where(t => !hidden.Contains(t.SourceId))
            .GroupBy(t => new { Genre = t.Genre ?? t.Album!.Genre, t.AlbumId, t.Album!.CoverArtKey })
            .Select(g => new { g.Key.Genre, g.Key.AlbumId, g.Key.CoverArtKey, Tracks = g.Count() })
            .Where(r => r.Genre != null && r.Genre != "")
            .ToListAsync(ct);

        return rows
            .SelectMany(r => TagValues.Split(r.Genre).Select(name => (Name: name, Row: r)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var albums = g.GroupBy(x => x.Row.AlbumId)
                    .Select(a => (Key: a.First().Row.CoverArtKey, Tracks: a.Sum(x => x.Row.Tracks)))
                    .ToList();
                return new GenreSummary(
                    g.First().Name,
                    albums.Count,
                    albums.Sum(a => a.Tracks),
                    albums.OrderByDescending(a => a.Tracks).Select(a => a.Key).OfType<string>().Distinct().ToList());
            })
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<Track>> GetGenreTracksAsync(string genre, CancellationToken ct = default)
    {
        var hidden = Hidden();
        // LIKE narrows it down in SQL (ASCII case-insensitive); the exact value match is done after splitting.
        var pattern = "%" + genre.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        await using var db = await _factory.CreateDbContextAsync(ct);
        var candidates = await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => !hidden.Contains(t.SourceId)
                        && EF.Functions.Like(t.Genre ?? t.Album!.Genre ?? "", pattern, @"\"))
            .OrderByDescending(t => t.Album!.Year).ThenBy(t => t.Album!.SortTitle)
            .ThenBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.Title)
            .ToListAsync(ct);
        return candidates
            .Where(t => TagValues.Split(t.Genre ?? t.Album?.Genre).Contains(genre, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<IReadOnlyList<Album>> GetRecentlyAddedAlbumsAsync(int count, CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking()
            .Include(a => a.AlbumArtist)
            .Where(a => a.Tracks.Any(t => !hidden.Contains(t.SourceId)))
            .OrderByDescending(a => a.DateAdded)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int count, CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => !hidden.Contains(t.SourceId))
            .OrderByDescending(t => t.DateAdded)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetRecentlyPlayedAsync(int count, CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.LastPlayedAt != null && !hidden.Contains(t.SourceId))
            .OrderByDescending(t => t.LastPlayedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetMostPlayedAsync(int count, CancellationToken ct = default)
    {
        var hidden = Hidden();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.PlayCount > 0 && !hidden.Contains(t.SourceId))
            .OrderByDescending(t => t.PlayCount)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Playlists.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<Playlist?> GetPlaylistAsync(int playlistId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
    }

    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(int playlistId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.PlaylistTracks.AsNoTracking()
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .Include(pt => pt.Track).ThenInclude(t => t!.Album).ThenInclude(a => a!.AlbumArtist)
            .Select(pt => pt.Track!)
            .ToListAsync(ct);
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var playlist = new Playlist { Name = name, DateCreated = DateTimeOffset.UtcNow };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(ct);
        return playlist;
    }

    public async Task DeletePlaylistAsync(int playlistId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Playlists.Where(p => p.Id == playlistId).ExecuteDeleteAsync(ct);
    }

    public async Task AddTracksToPlaylistAsync(int playlistId, IReadOnlyList<int> trackIds, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var nextPos = await db.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .Select(pt => (int?)pt.Position)
            .MaxAsync(ct) ?? -1;

        foreach (var trackId in trackIds)
            db.PlaylistTracks.Add(new PlaylistTrack { PlaylistId = playlistId, TrackId = trackId, Position = ++nextPos });

        await db.SaveChangesAsync(ct);
    }

    public async Task RemovePlaylistEntryAsync(int playlistId, int index, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var items = await db.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .ToListAsync(ct);
        if (index < 0 || index >= items.Count) return;

        db.PlaylistTracks.Remove(items[index]);
        items.RemoveAt(index);
        for (var i = 0; i < items.Count; i++) items[i].Position = i;
        await db.SaveChangesAsync(ct);
    }

    public async Task MovePlaylistEntryAsync(int playlistId, int fromIndex, int toIndex, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var items = await db.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .OrderBy(pt => pt.Position)
            .ToListAsync(ct);
        if (fromIndex < 0 || fromIndex >= items.Count || toIndex < 0 || toIndex >= items.Count) return;

        var item = items[fromIndex];
        items.RemoveAt(fromIndex);
        items.Insert(toIndex, item);
        for (var i = 0; i < items.Count; i++) items[i].Position = i;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordPlayAsync(int trackId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        db.PlayEvents.Add(new PlayEvent { TrackId = trackId, PlayedAt = now });
        await db.Tracks.Where(t => t.Id == trackId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.PlayCount, t => t.PlayCount + 1)
                .SetProperty(t => t.LastPlayedAt, now), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SourceConfig>> GetSourcesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Sources.AsNoTracking().ToListAsync(ct);
    }

    public async Task<SourceConfig> UpsertSourceAsync(SourceConfig source, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var exists = await db.Sources.AnyAsync(s => s.Id == source.Id, ct);
        if (exists) db.Sources.Update(source);
        else db.Sources.Add(source);
        await db.SaveChangesAsync(ct);
        return source;
    }

    public async Task DeleteSourceAsync(string sourceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Tracks.Where(t => t.SourceId == sourceId).ExecuteDeleteAsync(ct);
        await db.Sources.Where(s => s.Id == sourceId).ExecuteDeleteAsync(ct);
        // Clean up albums/artists that no longer have tracks.
        await db.Albums.Where(a => !a.Tracks.Any()).ExecuteDeleteAsync(ct);
        await db.Artists.Where(a => !a.Albums.Any()).ExecuteDeleteAsync(ct);
    }
}
