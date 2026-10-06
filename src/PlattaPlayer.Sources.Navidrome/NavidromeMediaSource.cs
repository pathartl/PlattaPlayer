using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Navidrome;

/// <summary>
/// A media source backed by a Navidrome (or other Subsonic-compatible) server. The Subsonic API has no "every
/// song" call, so the library is walked album by album (ID3 tags, which also gives every track its album artist);
/// tracks resolve to direct stream URLs that the playback engine plays over HTTP.
/// </summary>
public sealed class NavidromeMediaSource : IMediaSource
{
    // getAlbumList2 returns at most 500 albums per call.
    private const int AlbumPageSize = 500;

    // Albums fetched at once while walking a page; enough to hide latency without hammering small servers.
    private const int AlbumConcurrency = 6;

    private readonly NavidromeClient _client;

    // Cover-art id of each track (its own, else its album's), remembered during enumeration.
    private readonly ConcurrentDictionary<string, string> _coverArt = new();

    public NavidromeMediaSource(string id, string displayName, NavidromeSourceSettings settings)
    {
        Id = id;
        DisplayName = displayName;
        _client = NavidromeClient.Build(settings);
    }

    public string Id { get; }
    public SourceType Type => SourceType.Navidrome;
    public string DisplayName { get; }

    public async IAsyncEnumerable<SourceTrack> EnumerateTracksAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var albums = await _client.GetAlbumsAsync(offset, AlbumPageSize, ct);
            if (albums.Count == 0)
                yield break;

            foreach (var chunk in albums.Where(a => !string.IsNullOrEmpty(a.Id)).Chunk(AlbumConcurrency))
            {
                var details = await Task.WhenAll(chunk.Select(a => _client.GetAlbumAsync(a.Id!, ct)));
                foreach (var album in details)
                {
                    if (album?.Song is null)
                        continue; // Removed since the album list was read.

                    foreach (var song in album.Song)
                    {
                        var track = MapTrack(song, album);
                        if (track is null)
                            continue;
                        if ((song.CoverArt ?? album.CoverArt) is { Length: > 0 } coverArt)
                            _coverArt[track.SourceItemId] = coverArt;
                        yield return track;
                    }
                }
            }

            offset += albums.Count;
            if (albums.Count < AlbumPageSize)
                yield break;
        }
    }

    public Task<PlayableMedia> ResolvePlayableAsync(Track track, CancellationToken ct = default)
        => Task.FromResult(new PlayableMedia(PlayableKind.RemoteUrl, _client.StreamUrl(track.SourceItemId)));

    public async Task<byte[]?> GetCoverArtAsync(SourceTrack track, CancellationToken ct = default)
    {
        try
        {
            if (!_coverArt.TryGetValue(track.SourceItemId, out var coverArt))
            {
                // Not seen in this session's enumeration (e.g. art fetched after a restart): look the song up.
                var song = await _client.GetSongAsync(track.SourceItemId, ct);
                coverArt = song?.CoverArt;
                if (string.IsNullOrEmpty(coverArt))
                    return null;
                _coverArt[track.SourceItemId] = coverArt;
            }
            return await _client.GetCoverArtAsync(coverArt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private SourceTrack? MapTrack(NavidromeClient.Song song, NavidromeClient.Album album)
    {
        if (string.IsNullOrEmpty(song.Id) || song.IsDir || song.IsVideo)
            return null;

        var artists = TagValues.Normalize(song.Artists?.Select(a => a.Name)) ?? TagValues.Normalize(song.Artist);
        var albumArtist = TagValues.Normalize(song.AlbumArtists?.Select(a => a.Name))
            ?? TagValues.Normalize(album.Artists?.Select(a => a.Name))
            ?? TagValues.Normalize(album.Artist)
            ?? artists
            ?? "Unknown Artist";

        var trackArtist = artists ?? albumArtist;
        var albumTitle = FirstNonBlank(song.Album, album.Name) ?? "Unknown Album";

        return new SourceTrack
        {
            SourceId = Id,
            SourceItemId = song.Id,
            LocalPath = null,
            Title = FirstNonBlank(song.Title) ?? "Unknown",
            AlbumTitle = albumTitle,
            AlbumArtist = albumArtist,
            TrackArtist = trackArtist,
            TrackNo = song.Track,
            DiscNo = song.DiscNumber,
            Year = song.Year ?? album.Year,
            Duration = song.Duration is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero,
            Genre = TagValues.Normalize(song.Genres?.Select(g => g.Name))
                ?? TagValues.Normalize(song.Genre)
                ?? TagValues.Normalize(album.Genres?.Select(g => g.Name))
                ?? TagValues.Normalize(album.Genre),
            Format = song.Suffix ?? string.Empty,
        };
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
