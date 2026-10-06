using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Emby;

/// <summary>
/// A media source backed by an Emby server. Enumerates the user's audio library over the REST API and
/// resolves tracks to direct stream URLs that the playback engine plays over HTTP.
/// </summary>
public sealed class EmbyMediaSource : IMediaSource
{
    private const int PageSize = 200;

    private readonly EmbyClient _client;

    // Item whose primary image is each track's cover (the track itself, or its album when the track has no
    // art of its own — Emby doesn't fall back to the album), remembered during enumeration.
    private readonly ConcurrentDictionary<string, string> _coverItems = new();

    public EmbyMediaSource(string id, string displayName, EmbySourceSettings settings)
    {
        Id = id;
        DisplayName = displayName;
        _client = EmbyClient.Build(settings);
    }

    public string Id { get; }
    public SourceType Type => SourceType.Emby;
    public string DisplayName { get; }

    public async IAsyncEnumerable<SourceTrack> EnumerateTracksAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var startIndex = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await _client.GetAudioItemsAsync(startIndex, PageSize, ct);
            var items = page?.Items;
            if (items is null || items.Count == 0)
                yield break;

            foreach (var item in items)
            {
                var track = MapTrack(item);
                if (track is null)
                    continue;
                if (CoverItemOf(item) is { } coverItem)
                    _coverItems[track.SourceItemId] = coverItem;
                yield return track;
            }

            startIndex += items.Count;
            if (items.Count < PageSize)
                yield break;
        }
    }

    public Task<PlayableMedia> ResolvePlayableAsync(Track track, CancellationToken ct = default)
        => Task.FromResult(new PlayableMedia(PlayableKind.RemoteUrl, _client.StreamUrl(track.SourceItemId)));

    public async Task<byte[]?> GetCoverArtAsync(SourceTrack track, CancellationToken ct = default)
    {
        try
        {
            if (!_coverItems.TryGetValue(track.SourceItemId, out var coverItem))
            {
                // Not seen in this session's enumeration (e.g. art fetched after a restart): look the track up.
                var item = await _client.GetItemAsync(track.SourceItemId, ct);
                coverItem = item is null ? null : CoverItemOf(item);
                if (coverItem is null)
                    return null;
                _coverItems[track.SourceItemId] = coverItem;
            }
            return await _client.GetPrimaryImageAsync(coverItem, ct);
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

    private static string? CoverItemOf(EmbyClient.BaseItem item)
    {
        if (item.HasPrimaryImage)
            return item.Id;
        if (!string.IsNullOrEmpty(item.AlbumId) && !string.IsNullOrEmpty(item.AlbumPrimaryImageTag))
            return item.AlbumId;
        return null;
    }

    private SourceTrack? MapTrack(EmbyClient.BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Id))
            return null;

        var artists = TagValues.Normalize(item.Artists);
        var albumArtist = TagValues.Normalize(item.AlbumArtists?.Select(a => a.Name))
            ?? TagValues.Normalize(item.AlbumArtist)
            ?? artists
            ?? "Unknown Artist";

        var trackArtist = artists ?? albumArtist;
        var album = string.IsNullOrWhiteSpace(item.Album) ? "Unknown Album" : item.Album!;

        return new SourceTrack
        {
            SourceId = Id,
            SourceItemId = item.Id,
            LocalPath = null,
            Title = string.IsNullOrWhiteSpace(item.Name) ? "Unknown" : item.Name!,
            AlbumTitle = album,
            AlbumArtist = albumArtist,
            TrackArtist = trackArtist,
            TrackNo = item.IndexNumber,
            DiscNo = item.ParentIndexNumber,
            Year = item.ProductionYear,
            Duration = item.RunTimeTicks is { } ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero,
            Genre = TagValues.Normalize(item.Genres),
            Format = item.Container ?? string.Empty,
        };
    }
}
