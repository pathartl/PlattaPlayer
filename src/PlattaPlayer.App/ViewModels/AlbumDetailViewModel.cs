using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.App.Formatting;
using PlattaPlayer.App.Services;
using PlattaPlayer.App.ViewModels.Items;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.ViewModels;

/// <summary>"Disc N" sub-header between groups on multi-disc albums.</summary>
public sealed record DiscHeader(string Text);

/// <summary>One credited album artist, linked to its artist page; <see cref="IsLast"/> drops the trailing comma.</summary>
public sealed record AlbumArtistLink(int ArtistId, string Name, bool IsLast);

public sealed partial class AlbumDetailViewModel : PageViewModelBase
{
    private readonly ILibraryRepository _repository;
    private readonly ICoverArtCache _covers;
    private readonly IPlaybackService _playback;
    private readonly INavigationService _navigation;

    public AlbumDetailViewModel(ILibraryRepository repository, ICoverArtCache covers,
        IPlaybackService playback, INavigationService navigation)
    {
        _repository = repository;
        _covers = covers;
        _playback = playback;
        _navigation = navigation;
    }

    public int AlbumId { get; set; }

    public override string Title => AlbumTitle;

    [ObservableProperty] private string _albumTitle = string.Empty;
    [ObservableProperty] private string _albumArtist = string.Empty;
    [ObservableProperty] private string? _coverPath;

    /// <summary>The album artist credit split into its artists, each linking to its own page.</summary>
    [ObservableProperty] private IReadOnlyList<AlbumArtistLink> _albumArtists = Array.Empty<AlbumArtistLink>();

    /// <summary>"ALBUM · 2021".</summary>
    [ObservableProperty] private string _eyebrow = string.Empty;

    /// <summary>Genre, track count and runtime, e.g. ["Ambient", "10 tracks, 47 min"].</summary>
    [ObservableProperty] private IReadOnlyList<string> _details = Array.Empty<string>();

    /// <summary>Format badges under the art: "FLAC", "24-bit / 192 kHz".</summary>
    [ObservableProperty] private IReadOnlyList<string> _badges = Array.Empty<string>();

    /// <summary>Tracks in play order.</summary>
    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    /// <summary>Rows for the track list: tracks, with <see cref="DiscHeader"/>s on multi-disc albums.</summary>
    public ObservableCollection<object> Rows { get; } = new();

    /// <summary>Playlists available as "Add to playlist" targets in the row context menu.</summary>
    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    public override async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var album = await _repository.GetAlbumAsync(AlbumId);
            var tracks = await _repository.GetAlbumTracksAsync(AlbumId);

            Tracks.Clear();
            Rows.Clear();
            var multiDisc = tracks.Select(t => t.DiscNo ?? 1).Distinct().Count() > 1;
            int? disc = null;
            foreach (var t in tracks)
            {
                if (multiDisc && (t.DiscNo ?? 1) != disc)
                {
                    disc = t.DiscNo ?? 1;
                    Rows.Add(new DiscHeader($"Disc {disc}"));
                }
                var item = new TrackItemViewModel(t, _covers);
                Tracks.Add(item);
                Rows.Add(item);
            }

            if (album is not null)
            {
                AlbumTitle = album.Title;
                AlbumArtist = TagValues.Display(album.ArtistCredit) is { Length: > 0 } credit ? credit : "Unknown Artist";

                // Credit order, not the join table's: the first-credited artist leads.
                var credited = TagValues.Split(album.ArtistCredit)
                    .Select(n => album.Artists.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .OfType<Artist>()
                    .ToList();
                AlbumArtists = credited.Count == 0
                    ? [new AlbumArtistLink(album.AlbumArtistId, AlbumArtist, true)]
                    : credited.Select((a, i) => new AlbumArtistLink(a.Id, a.Name, i == credited.Count - 1)).ToList();
                CoverPath = _covers.GetPath(album.CoverArtKey);

                var type = new AlbumItemViewModel(album, _covers, tracks).ReleaseType.ToUpperInvariant();
                Eyebrow = album.Year is { } y ? $"{type} · {y}" : type;

                var details = new List<string>();
                var genre = album.Genre ?? tracks.Select(t => t.Genre).FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
                if (TagValues.Display(genre) is { Length: > 0 } genres) details.Add(genres);
                var runtime = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
                details.Add($"{AudioFormatText.Count(tracks.Count, "track", "tracks")}, {AudioFormatText.Runtime(runtime)}");
                Details = details;

                // Badges describe the album's dominant format.
                var lead = tracks.GroupBy(AudioFormatText.WithCodec).OrderByDescending(g => g.Count()).FirstOrDefault()?.First();
                Badges = lead is null
                    ? Array.Empty<string>()
                    : new[] { AudioFormatText.Codec(lead), AudioFormatText.Long(lead) }.Where(s => s.Length > 0).ToArray();
                OnPropertyChanged(nameof(Title));
            }

            Playlists.Clear();
            foreach (var p in await _repository.GetPlaylistsAsync())
                Playlists.Add(new PlaylistItemViewModel(p));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task PlayAll()
    {
        _playback.ShuffleEnabled = false;
        return Tracks.Count == 0 ? Task.CompletedTask : _playback.PlayQueueAsync(Tracks.Select(t => t.Track).ToList(), 0);
    }

    [RelayCommand]
    private Task Shuffle()
    {
        if (Tracks.Count == 0) return Task.CompletedTask;
        _playback.ShuffleEnabled = true;
        return _playback.PlayQueueAsync(Tracks.Select(t => t.Track).ToList(), Random.Shared.Next(Tracks.Count));
    }

    /// <summary>Starts <paramref name="item"/> and queues the rest of the album after it.</summary>
    [RelayCommand]
    private Task Play(TrackItemViewModel item)
        => _playback.PlayQueueAsync(Tracks.Select(t => t.Track).ToList(), Tracks.IndexOf(item));

    [RelayCommand]
    private void OpenArtists() => _navigation.NavigateTo<ArtistsViewModel>();

    [RelayCommand]
    private void OpenArtist(int artistId)
    {
        if (artistId > 0) _navigation.NavigateTo<ArtistDetailViewModel>(vm => vm.ArtistId = artistId);
    }

    public Task AddToPlaylistAsync(TrackItemViewModel track, int playlistId)
        => _repository.AddTracksToPlaylistAsync(playlistId, new[] { track.Track.Id });
}
