using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Core.Abstractions;

/// <summary>
/// Read/write access to the local library used by the UI. All reads come from here so views
/// never block on a live source scan.
/// </summary>
public interface ILibraryRepository
{
    // Browsing
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetAllTracksAsync(CancellationToken ct = default);
    Task<Album?> GetAlbumAsync(int albumId, CancellationToken ct = default);
    Task<Artist?> GetArtistAsync(int artistId, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetAlbumTracksAsync(int albumId, CancellationToken ct = default);
    /// <summary>All tracks on the artist's albums, newest album first, then disc/track order.</summary>
    Task<IReadOnlyList<Track>> GetArtistTracksAsync(int artistId, CancellationToken ct = default);
    Task<LibraryCounts> GetLibraryCountsAsync(CancellationToken ct = default);
    /// <summary>Track count per album-artist id.</summary>
    Task<IReadOnlyDictionary<int, int>> GetTrackCountsByArtistAsync(CancellationToken ct = default);
    /// <summary>Every genre value in the library, by name.</summary>
    Task<IReadOnlyList<GenreSummary>> GetGenresAsync(CancellationToken ct = default);
    /// <summary>Tracks having <paramref name="genre"/> among their genre values (case-insensitive), newest album
    /// first, then disc/track order.</summary>
    Task<IReadOnlyList<Track>> GetGenreTracksAsync(string genre, CancellationToken ct = default);

    // Home sections
    Task<IReadOnlyList<Album>> GetRecentlyAddedAlbumsAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetRecentlyAddedAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetRecentlyPlayedAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetMostPlayedAsync(int count, CancellationToken ct = default);

    // Playlists
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken ct = default);
    Task<Playlist?> GetPlaylistAsync(int playlistId, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(int playlistId, CancellationToken ct = default);
    Task<Playlist> CreatePlaylistAsync(string name, CancellationToken ct = default);
    Task DeletePlaylistAsync(int playlistId, CancellationToken ct = default);
    Task AddTracksToPlaylistAsync(int playlistId, IReadOnlyList<int> trackIds, CancellationToken ct = default);
    /// <summary>Removes the entry at <paramref name="index"/> (position order); duplicate-safe.</summary>
    Task RemovePlaylistEntryAsync(int playlistId, int index, CancellationToken ct = default);
    /// <summary>Moves the entry at <paramref name="fromIndex"/> to <paramref name="toIndex"/> (position order); duplicate-safe.</summary>
    Task MovePlaylistEntryAsync(int playlistId, int fromIndex, int toIndex, CancellationToken ct = default);

    // Playback stats
    Task RecordPlayAsync(int trackId, CancellationToken ct = default);

    // Sources
    Task<IReadOnlyList<SourceConfig>> GetSourcesAsync(CancellationToken ct = default);
    Task<SourceConfig> UpsertSourceAsync(SourceConfig source, CancellationToken ct = default);
    Task DeleteSourceAsync(string sourceId, CancellationToken ct = default);
}
