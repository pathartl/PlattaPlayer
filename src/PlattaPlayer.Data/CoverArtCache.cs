using System.Security.Cryptography;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.Data;

/// <summary>
/// File-backed cover-art cache. Images are content-addressed by SHA-256 hash and written once
/// to <see cref="AppPaths.CoversDirectory"/>; the database stores only the hash key.
/// </summary>
public sealed class CoverArtCache : ICoverArtCache
{
    private readonly string _dir;

    public CoverArtCache() : this(AppPaths.CoversDirectory) { }

    public CoverArtCache(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public async Task<string?> SaveAsync(byte[]? imageBytes, CancellationToken ct = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return null;

        var key = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var path = PathForKey(key);
        if (!File.Exists(path))
            await File.WriteAllBytesAsync(path, imageBytes, ct).ConfigureAwait(false);

        return key;
    }

    public string? GetPath(string? coverArtKey)
    {
        if (string.IsNullOrEmpty(coverArtKey))
            return null;

        var path = PathForKey(coverArtKey);
        return File.Exists(path) ? path : null;
    }

    private string PathForKey(string key) => Path.Combine(_dir, key + ".img");
}
