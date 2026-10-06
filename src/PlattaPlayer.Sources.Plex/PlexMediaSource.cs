using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;
using Plex.ServerApi.Clients.Interfaces;
using Plex.ServerApi.Enums;
using Plex.ServerApi.PlexModels.Media;

namespace PlattaPlayer.Sources.Plex;

/// <summary>
/// A media source backed by a Plex Media Server. Enumerates every music library on the server via Plex.Api
/// and resolves tracks to direct (untranscoded) part URLs that the playback engine plays over HTTP.
/// </summary>
public sealed class PlexMediaSource : IMediaSource
{
    /// <summary>Plex's section type for music libraries.</summary>
    internal const string MusicSectionType = "artist";

    // Plex streamType of an audio stream within a media part.
    private const int AudioStreamType = 2;

    private const int PageSize = 200;

    private readonly PlexSourceSettings _settings;
    private readonly IPlexServerClient _serverClient;
    private readonly IPlexLibraryClient _libraryClient;

    // Album art path per track rating key, remembered during enumeration so cover fetches (which only
    // get the track's id) need no extra metadata round trip.
    private readonly ConcurrentDictionary<string, string> _thumbs = new();

    public PlexMediaSource(string id, string displayName, PlexSourceSettings settings)
    {
        Id = id;
        DisplayName = displayName;
        _settings = settings;
        _serverClient = PlexClientFactory.ServerClient(settings.DeviceId);
        _libraryClient = PlexClientFactory.LibraryClient(settings.DeviceId);
    }

    public string Id { get; }
    public SourceType Type => SourceType.Plex;
    public string DisplayName { get; }

    private string ServerUrl => _settings.ServerUrl;
    private string Token => _settings.AccessToken;

    public async IAsyncEnumerable<SourceTrack> EnumerateTracksAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var libraries = await _serverClient.GetLibrariesAsync(Token, ServerUrl).WaitAsync(ct);
        var sections = libraries?.Libraries?.Where(l => l.Type == MusicSectionType) ?? [];

        foreach (var section in sections)
        {
            // Plex keeps year and genres on albums rather than tracks, so read the albums first.
            var albums = new Dictionary<string, Metadata>();
            await foreach (var album in PageAsync(section.Key, SearchType.Album, ct))
                if (!string.IsNullOrEmpty(album.RatingKey))
                    albums[album.RatingKey] = album;

            await foreach (var item in PageAsync(section.Key, SearchType.Track, ct))
            {
                var track = MapTrack(item, albums);
                if (track is not null)
                    yield return track;
            }
        }
    }

    public async Task<PlayableMedia> ResolvePlayableAsync(Track track, CancellationToken ct = default)
    {
        // The part key names the file itself (it changes when the file is replaced), so look it up at play
        // time rather than storing it. Streaming the part serves the original file, untranscoded.
        var item = await GetItemAsync(track.SourceItemId, ct);
        var partKey = item?.Media?.SelectMany(m => m.Part ?? []).FirstOrDefault(p => !string.IsNullOrEmpty(p.Key))?.Key
            ?? throw new InvalidOperationException($"Plex has no playable file for \"{track.Title}\".");

        var url = $"{ServerUrl}{partKey}?X-Plex-Token={Uri.EscapeDataString(Token)}";
        return new PlayableMedia(PlayableKind.RemoteUrl, url);
    }

    public async Task<byte[]?> GetCoverArtAsync(SourceTrack track, CancellationToken ct = default)
    {
        try
        {
            if (!_thumbs.TryGetValue(track.SourceItemId, out var thumb))
            {
                var item = await GetItemAsync(track.SourceItemId, ct);
                thumb = FirstNonEmpty(item?.ParentThumb, item?.Thumb);
            }
            if (thumb is null)
                return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}{thumb}");
            request.Headers.Add("X-Plex-Token", Token);
            using var response = await PlexClientFactory.Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Every item of one type in a library section, a page at a time.</summary>
    private async IAsyncEnumerable<Metadata> PageAsync(
        string sectionKey, SearchType type, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await _libraryClient
                .LibrarySearch(Token, ServerUrl, null, sectionKey, null, type, null, start, PageSize)
                .WaitAsync(ct);

            var items = page?.Media;
            if (items is null || items.Count == 0)
                yield break;

            foreach (var item in items)
                yield return item;

            start += items.Count;
            if (items.Count < PageSize || (page!.TotalSize > 0 && start >= page.TotalSize))
                yield break;
        }
    }

    private async Task<Metadata?> GetItemAsync(string ratingKey, CancellationToken ct)
    {
        var container = await _libraryClient.GetItem(Token, ServerUrl, ratingKey).WaitAsync(ct);
        return container?.Media?.FirstOrDefault();
    }

    private SourceTrack? MapTrack(Metadata item, IReadOnlyDictionary<string, Metadata> albums)
    {
        if (string.IsNullOrEmpty(item.RatingKey))
            return null;

        var album = item.ParentRatingKey is { } albumKey ? albums.GetValueOrDefault(albumKey) : null;

        // Plex files a track under its album artist (grandparent); originalTitle holds the track's own
        // artist when it differs, as on compilations.
        var albumArtist = TagValues.Normalize(item.GrandparentTitle) ?? "Unknown Artist";
        var trackArtist = TagValues.Normalize(item.OriginalTitle) ?? albumArtist;
        var albumTitle = FirstNonEmpty(item.ParentTitle, album?.Title) ?? "Unknown Album";

        var medium = item.Media?.FirstOrDefault();
        var part = medium?.Part?.FirstOrDefault();
        var audio = part?.Stream?.FirstOrDefault(s => s.StreamType == AudioStreamType);

        var thumb = FirstNonEmpty(item.ParentThumb, album?.Thumb, item.Thumb);
        if (thumb is not null)
            _thumbs[item.RatingKey] = thumb;

        return new SourceTrack
        {
            SourceId = Id,
            SourceItemId = item.RatingKey,
            LocalPath = null,
            Title = FirstNonEmpty(item.Title) ?? "Unknown",
            AlbumTitle = albumTitle,
            AlbumArtist = albumArtist,
            TrackArtist = trackArtist,
            TrackNo = item.Index > 0 ? item.Index : null,
            DiscNo = item.ParentIndex > 0 ? item.ParentIndex : null,
            Year = item.Year > 0 ? item.Year : album?.Year > 0 ? album.Year : null,
            Duration = TimeSpan.FromMilliseconds(item.Duration > 0 ? item.Duration : medium?.Duration ?? 0),
            Genre = TagValues.Normalize(item.Genres?.Select(g => g.Tag))
                    ?? TagValues.Normalize(album?.Genres?.Select(g => g.Tag)),
            Format = FirstNonEmpty(medium?.Container, part?.Container, medium?.AudioCodec) ?? string.Empty,
            SampleRate = audio?.SamplingRate > 0 ? audio.SamplingRate : null,
            BitsPerSample = audio?.BitDepth > 0 ? audio.BitDepth : null,
            Bitrate = medium?.Bitrate > 0 ? (int)medium.Bitrate : null,
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
