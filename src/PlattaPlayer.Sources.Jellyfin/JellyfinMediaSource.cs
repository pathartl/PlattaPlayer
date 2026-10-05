using System.Runtime.CompilerServices;
using Jellyfin.Sdk.Generated.Models;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Sources.Jellyfin;

/// <summary>
/// A media source backed by a Jellyfin server. Enumerates the user's audio library via the SDK and
/// resolves tracks to direct stream URLs that the playback engine plays over HTTP.
/// </summary>
public sealed class JellyfinMediaSource : IMediaSource
{
    private const int PageSize = 200;

    private readonly JellyfinSourceSettings _settings;
    private readonly global::Jellyfin.Sdk.JellyfinApiClient _client;
    private readonly HttpClient _http = new();

    public JellyfinMediaSource(string id, string displayName, JellyfinSourceSettings settings)
    {
        Id = id;
        DisplayName = displayName;
        _settings = settings;
        _client = JellyfinClientFactory.Build(settings);
    }

    public string Id { get; }
    public SourceType Type => SourceType.Jellyfin;
    public string DisplayName { get; }

    public async IAsyncEnumerable<SourceTrack> EnumerateTracksAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userId = Guid.Parse(_settings.UserId);
        var startIndex = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await _client.Items.GetAsync(config =>
            {
                var q = config.QueryParameters;
                q.UserId = userId;
                q.Recursive = true;
                q.IncludeItemTypes = new[] { BaseItemKind.Audio };
                q.SortBy = new[] { ItemSortBy.AlbumArtist, ItemSortBy.Album, ItemSortBy.IndexNumber };
                q.SortOrder = new[] { SortOrder.Ascending };
                q.Fields = new[] { ItemFields.Genres };
                q.StartIndex = startIndex;
                q.Limit = PageSize;
            }, ct);

            var items = page?.Items;
            if (items is null || items.Count == 0)
                yield break;

            foreach (var item in items)
            {
                var track = MapTrack(item);
                if (track is not null)
                    yield return track;
            }

            startIndex += items.Count;
            if (items.Count < PageSize)
                yield break;
        }
    }

    public Task<PlayableMedia> ResolvePlayableAsync(Track track, CancellationToken ct = default)
    {
        // Direct (static) stream of the original file; the engine plays it as a remote URL.
        var url = $"{_settings.ServerUrl}/Audio/{track.SourceItemId}/stream" +
                  $"?static=true&api_key={Uri.EscapeDataString(_settings.AccessToken)}";
        return Task.FromResult(new PlayableMedia(PlayableKind.RemoteUrl, url));
    }

    public async Task<byte[]?> GetCoverArtAsync(SourceTrack track, CancellationToken ct = default)
    {
        // Request the item's primary image; Jellyfin serves the album's art for tracks lacking their own.
        var url = $"{_settings.ServerUrl}/Items/{track.SourceItemId}/Images/Primary";
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private SourceTrack? MapTrack(BaseItemDto item)
    {
        if (item.Id is null)
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
            SourceItemId = item.Id.Value.ToString("N"),
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
