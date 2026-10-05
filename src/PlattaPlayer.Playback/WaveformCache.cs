using System.Security.Cryptography;
using System.Text;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.Playback;

/// <summary>
/// On-disk cache of seek-bar envelopes, so a track is decoded for its waveform only once. Each envelope is
/// a file of raw floats named by a hash of the track's identity, so neither paths nor credentials appear on
/// disk. Failures are swallowed: the cache is an optimisation, never a reason to lose the waveform.
/// </summary>
internal sealed class WaveformCache(string directory)
{
    // Query parameters that carry credentials rather than identify content (Jellyfin uses api_key). A
    // re-login must not invalidate the cache, and the token must not leak into the key.
    private static readonly HashSet<string> CredentialParameters =
        new(StringComparer.OrdinalIgnoreCase) { "api_key", "apikey", "token", "access_token" };

    /// <summary>
    /// The identity of what <paramref name="media"/> sounds like, or null when it cannot be pinned down.
    /// <paramref name="variant"/> is what else the sound depends on (a codec's <c>ICodecDecoder.Variant</c>,
    /// e.g. the SoundFont that rendered a MIDI file). <paramref name="version"/> describes the envelope
    /// algorithm, so changing it invalidates old entries.
    /// </summary>
    public static string? KeyFor(PlayableMedia media, string? variant, string version)
    {
        var identity = media.Kind == PlayableKind.RemoteUrl
            ? StripCredentials(media.Location)
            : FileIdentity(media.Location);
        if (identity is null) return null;

        if (media.Subsong is { } subsong) identity += "::" + subsong;
        if (variant is not null) identity += "|" + variant;
        return version + "|" + identity;
    }

    public float[]? TryLoad(string key, int length)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != length * sizeof(float)) return null;

            var envelope = new float[length];
            Buffer.BlockCopy(bytes, 0, envelope, 0, bytes.Length);
            return envelope;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string key, float[] envelope)
    {
        try
        {
            var bytes = new byte[envelope.Length * sizeof(float)];
            Buffer.BlockCopy(envelope, 0, bytes, 0, bytes.Length);

            // Write then rename, so a crash mid-write never leaves a truncated entry behind.
            var path = PathFor(key);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string PathFor(string key)
        => Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".wf");

    /// <summary>Path, size and modification time: a re-encoded or rewritten (e.g. retagged) file gets a new entry.</summary>
    private static string? FileIdentity(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
            return $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string StripCredentials(string url)
    {
        var query = url.IndexOf('?');
        if (query < 0) return url;

        var kept = url[(query + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !CredentialParameters.Contains(Uri.UnescapeDataString(p.Split('=', 2)[0])));
        return url[..query] + "?" + string.Join('&', kept);
    }
}
