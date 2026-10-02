using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Data;

/// <summary>EF Core context backing the local SQLite library cache.</summary>
public class LibraryDbContext : DbContext
{
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options)
    {
    }

    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistTrack> PlaylistTracks => Set<PlaylistTrack>();
    public DbSet<PlayEvent> PlayEvents => Set<PlayEvent>();
    public DbSet<SourceConfig> Sources => Set<SourceConfig>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite cannot ORDER BY / compare a DateTimeOffset stored as TEXT. Store it as a
        // sortable long instead so recently-added / recently-played queries can run in SQL.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Artist>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.SortName);
            e.Property(x => x.Name).IsRequired();
        });

        b.Entity<Album>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AlbumArtistId, x.Title }).IsUnique();
            e.HasIndex(x => x.SortTitle);
            e.HasOne(x => x.AlbumArtist)
                .WithMany(a => a.Albums)
                .HasForeignKey(x => x.AlbumArtistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Track>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SourceId, x.SourceItemId }).IsUnique();
            e.HasIndex(x => x.LastPlayedAt);
            e.HasIndex(x => x.PlayCount);
            e.HasIndex(x => x.DateAdded);
            e.HasOne(x => x.Album)
                .WithMany(a => a.Tracks)
                .HasForeignKey(x => x.AlbumId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Playlist>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
        });

        b.Entity<PlaylistTrack>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PlaylistId, x.Position });
            e.HasOne(x => x.Playlist)
                .WithMany(p => p.Items)
                .HasForeignKey(x => x.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Track)
                .WithMany()
                .HasForeignKey(x => x.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlayEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PlayedAt);
            e.HasOne(x => x.Track)
                .WithMany()
                .HasForeignKey(x => x.TrackId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SourceConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
