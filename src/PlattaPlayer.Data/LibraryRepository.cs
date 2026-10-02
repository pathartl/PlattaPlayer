using Microsoft.EntityFrameworkCore;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Data;

/// <summary>EF Core implementation of <see cref="ILibraryRepository"/> backed by SQLite.</summary>
public sealed class LibraryRepository : ILibraryRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory;

    public LibraryRepository(IDbContextFactory<LibraryDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking()
            .Include(a => a.AlbumArtist)
            .OrderBy(a => a.SortTitle)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Artists.AsNoTracking()
            .Include(a => a.Albums)
            .OrderBy(a => a.SortName)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetAllTracksAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .OrderBy(t => t.Title)
            .ToListAsync(ct);
    }

    public async Task<Album?> GetAlbumAsync(int albumId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking()
            .Include(a => a.AlbumArtist)
            .FirstOrDefaultAsync(a => a.Id == albumId, ct);
    }

    public async Task<Artist?> GetArtistAsync(int artistId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Artists.AsNoTracking()
            .Include(a => a.Albums)
            .FirstOrDefaultAsync(a => a.Id == artistId, ct);
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(int albumId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.AlbumId == albumId)
            .OrderBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.Title)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetArtistTracksAsync(int artistId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.Album!.AlbumArtistId == artistId)
            .OrderByDescending(t => t.Album!.Year).ThenBy(t => t.Album!.SortTitle)
            .ThenBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.Title)
            .ToListAsync(ct);
    }

    public async Task<LibraryCounts> GetLibraryCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return new LibraryCounts(
            await db.Artists.CountAsync(ct),
            await db.Albums.CountAsync(ct),
            await db.Tracks.CountAsync(ct));
    }

    public async Task<IReadOnlyDictionary<int, int>> GetTrackCountsByArtistAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .GroupBy(t => t.Album!.AlbumArtistId)
            .Select(g => new { ArtistId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ArtistId, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<Album>> GetRecentlyAddedAlbumsAsync(int count, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking()
            .Include(a => a.AlbumArtist)
            .OrderByDescending(a => a.DateAdded)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int count, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .OrderByDescending(t => t.DateAdded)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetRecentlyPlayedAsync(int count, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.LastPlayedAt != null)
            .OrderByDescending(t => t.LastPlayedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetMostPlayedAsync(int count, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Include(t => t.Album).ThenInclude(a => a!.AlbumArtist)
            .Where(t => t.PlayCount > 0)
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
