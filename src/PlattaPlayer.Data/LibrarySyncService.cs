using Microsoft.EntityFrameworkCore;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Data;

/// <summary>
/// Pulls tracks from a configured <see cref="IMediaSource"/> into the SQLite library, building
/// artist/album/track rows and caching embedded cover art. Tracks no longer present at the source
/// are pruned so a rescan keeps the library in sync.
/// </summary>
public sealed class LibrarySyncService : ILibrarySyncService
{
    private readonly IDbContextFactory<LibraryDbContext> _factory;
    private readonly IMediaSourceManager _sources;
    private readonly ICoverArtCache _covers;

    public LibrarySyncService(
        IDbContextFactory<LibraryDbContext> factory,
        IMediaSourceManager sources,
        ICoverArtCache covers)
    {
        _factory = factory;
        _sources = sources;
        _covers = covers;
    }

    public async Task SyncAllAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var configs = await db.Sources.AsNoTracking().ToListAsync(ct);
        foreach (var config in configs)
            await SyncSourceAsync(config, progress, ct);
    }

    public async Task SyncSourceAsync(SourceConfig source, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var mediaSource = await _sources.GetAsync(source.Id, ct);

        await using var db = await _factory.CreateDbContextAsync(ct);

        var artistsByName = await db.Artists
            .ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, ct);
        var albumsByKey = await db.Albums
            .ToDictionaryAsync(a => AlbumKey(a.AlbumArtistId, a.Title), ct);
        var tracksByItemId = await db.Tracks
            .Where(t => t.SourceId == source.Id)
            .ToDictionaryAsync(t => t.SourceItemId, ct);

        var seenItemIds = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var processed = 0;

        await foreach (var st in mediaSource.EnumerateTracksAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            seenItemIds.Add(st.SourceItemId);

            await UpsertAsync(db, mediaSource, st, artistsByName, albumsByKey, tracksByItemId, now, refreshCover: false, ct);

            // Note: we deliberately defer SaveChanges to the end of the loop. Saving mid-loop would
            // assign real keys to newly-created artists and invalidate the album cache keys (which are
            // derived from the artist key), producing duplicate albums.
            if (++processed % 50 == 0)
                progress?.Report(new SyncProgress(source.DisplayName, processed, null));
        }

        // Prune tracks that vanished from the source since the last scan.
        var stale = tracksByItemId.Values.Where(t => t.Id != 0 && !seenItemIds.Contains(t.SourceItemId)).ToList();
        if (stale.Count > 0)
            db.Tracks.RemoveRange(stale);

        await db.SaveChangesAsync(ct);

        // Drop albums/artists left empty after pruning.
        await db.Albums.Where(a => !a.Tracks.Any()).ExecuteDeleteAsync(ct);
        await db.Artists.Where(a => !a.Albums.Any()).ExecuteDeleteAsync(ct);

        await db.Sources.Where(s => s.Id == source.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSyncedAt, now), ct);

        progress?.Report(new SyncProgress(source.DisplayName, processed, processed));
    }

    public async Task<int> SyncFilesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var updated = 0;
        List<SourceConfig> configs;
        await using (var configDb = await _factory.CreateDbContextAsync(ct))
            configs = await configDb.Sources.AsNoTracking().Where(s => s.Type == SourceType.Local).ToListAsync(ct);

        foreach (var config in configs)
        {
            if (await _sources.GetAsync(config.Id, ct) is not ILocalFileMediaSource source) continue;

            var owned = paths.Where(source.Owns).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (owned.Count == 0) continue;

            await using var db = await _factory.CreateDbContextAsync(ct);
            var artistsByName = await db.Artists
                .ToDictionaryAsync(a => a.Name, StringComparer.OrdinalIgnoreCase, ct);
            var albumsByKey = await db.Albums
                .ToDictionaryAsync(a => AlbumKey(a.AlbumArtistId, a.Title), ct);

            // Keyed by full path: the stored ids keep whatever form the source folder was configured in.
            var tracks = await db.Tracks.Where(t => t.SourceId == source.Id).ToListAsync(ct);
            var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tracks)
                byPath.TryAdd(SafeFullPath(t.SourceItemId), t);

            var tracksByItemId = new Dictionary<string, Track>(StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            foreach (var path in owned)
            {
                var st = source.ReadFile(path);
                if (byPath.TryGetValue(path, out var existing))
                {
                    if (st is null)
                    {
                        db.Tracks.Remove(existing); // gone from disk
                        continue;
                    }
                    tracksByItemId[st.SourceItemId] = existing;
                }
                else if (st is null)
                {
                    continue;
                }

                await UpsertAsync(db, source, st, artistsByName, albumsByKey, tracksByItemId, now, refreshCover: true, ct);
                updated++;
            }

            await db.SaveChangesAsync(ct);
            await db.Albums.Where(a => !a.Tracks.Any()).ExecuteDeleteAsync(ct);
            await db.Artists.Where(a => !a.Albums.Any()).ExecuteDeleteAsync(ct);
        }
        return updated;
    }

    /// <summary>Adds or updates the track for <paramref name="st"/>, creating its artist and album as
    /// needed. With <paramref name="refreshCover"/> the album's cover is re-read even if it has one (the
    /// sidecar image may have changed); otherwise only albums without a cover look for one.</summary>
    private async Task UpsertAsync(
        LibraryDbContext db, IMediaSource mediaSource, SourceTrack st,
        Dictionary<string, Artist> artistsByName, Dictionary<string, Album> albumsByKey,
        Dictionary<string, Track> tracksByItemId, DateTimeOffset now, bool refreshCover, CancellationToken ct)
    {
        var artist = GetOrCreateArtist(db, artistsByName, st.AlbumArtist);
        var album = GetOrCreateAlbum(db, albumsByKey, artist, st);

        if (album.CoverArtKey is null || refreshCover)
        {
            var bytes = await mediaSource.GetCoverArtAsync(st, ct);
            var key = await _covers.SaveAsync(bytes, ct);
            if (key is not null)
                album.CoverArtKey = key;
        }

        if (!tracksByItemId.TryGetValue(st.SourceItemId, out var track))
        {
            track = new Track { SourceId = st.SourceId, SourceItemId = st.SourceItemId, DateAdded = now };
            db.Tracks.Add(track);
            tracksByItemId[st.SourceItemId] = track;
        }

        track.Title = st.Title;
        track.Album = album;
        track.TrackArtist = st.TrackArtist;
        track.TrackNo = st.TrackNo;
        track.DiscNo = st.DiscNo;
        track.Duration = st.Duration;
        track.Format = st.Format;
        track.SampleRate = st.SampleRate;
        track.BitsPerSample = st.BitsPerSample;
        track.Bitrate = st.Bitrate;
        track.Genre = st.Genre;
        track.LocalPath = st.LocalPath;
        if (refreshCover && album.CoverArtKey is not null)
            track.CoverArtKey = album.CoverArtKey;
        else
            track.CoverArtKey ??= album.CoverArtKey;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static Artist GetOrCreateArtist(LibraryDbContext db, Dictionary<string, Artist> cache, string name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Unknown Artist" : name.Trim();
        if (cache.TryGetValue(clean, out var artist))
            return artist;

        artist = new Artist { Name = clean, SortName = SortKey(clean) };
        db.Artists.Add(artist);
        cache[clean] = artist;
        return artist;
    }

    private static Album GetOrCreateAlbum(LibraryDbContext db, Dictionary<string, Album> cache, Artist artist, SourceTrack st)
    {
        var title = string.IsNullOrWhiteSpace(st.AlbumTitle) ? "Unknown Album" : st.AlbumTitle.Trim();
        var key = AlbumKey(artist.Id, title, artist);
        if (cache.TryGetValue(key, out var album))
            return album;

        album = new Album
        {
            Title = title,
            SortTitle = SortKey(title),
            AlbumArtist = artist,
            Year = st.Year,
            Genre = st.Genre,
            DateAdded = DateTimeOffset.UtcNow
        };
        db.Albums.Add(album);
        cache[key] = album;
        return album;
    }

    // For a saved album we key by its FK; for a not-yet-saved artist (Id == 0) we fall back to the
    // artist reference identity so two albums under the same new artist still collide correctly.
    private static string AlbumKey(int artistId, string title) => $"{artistId}\u0001{title.ToLowerInvariant()}";

    private static string AlbumKey(int artistId, string title, Artist artist)
        => artistId != 0
            ? AlbumKey(artistId, title)
            : $"new:{artist.Name.ToLowerInvariant()}\u0001{title.ToLowerInvariant()}";

    private static string SortKey(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.StartsWith("the ", StringComparison.Ordinal) && v.Length > 4)
            v = v[4..];
        return v;
    }
}
