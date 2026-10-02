using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PlattaPlayer.Visualizations.PSP.Common;
using PlattaPlayer.Visualizations.PSP.Gu;

namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>The outcome of <see cref="PspFirmwareAssets.TryLoad"/>: either the assets or a user-facing error.</summary>
public sealed class PspFirmwareLoadResult
{
    private PspFirmwareLoadResult(PspFirmwareAssets? assets, string? error)
    {
        Assets = assets;
        Error = error;
    }

    public PspFirmwareAssets? Assets { get; }

    /// <summary>A message suitable for showing to the user when <see cref="Assets"/> is null.</summary>
    public string? Error { get; }

    public bool Success => Assets is not null;

    internal static PspFirmwareLoadResult Ok(PspFirmwareAssets assets) => new(assets, null);

    internal static PspFirmwareLoadResult Fail(string error) => new(null, error);
}

/// <summary>
/// The visualizers' non-code resources, extracted at runtime from an official PSP system software
/// update (EBOOT.PBP) supplied by the user: nothing from the firmware ships with the app.
/// <para>
/// The update's DATA.PSAR is decrypted with a managed port of pspdecrypt (see <see cref="PsarArchive"/>)
/// to obtain flash0:/vsh/module/visualizer_plugin.prx (decrypted + gunzipped: it holds the two embedded
/// JPEGs) and flash0:/vsh/resource/visualizer_plugin.rco (its GIM textures). Those two raw files are
/// cached in a directory of the caller's choosing, keyed by the EBOOT's path, size and timestamp, so the
/// PSAR is only decrypted once.
/// </para>
/// <para>Loading is safe on any thread; the resulting object is immutable and thread-safe.</para>
/// </summary>
public sealed class PspFirmwareAssets : IPspAssets
{
    public const string PrxPath = "flash0:/vsh/module/visualizer_plugin.prx";
    public const string RcoPath = "flash0:/vsh/resource/visualizer_plugin.rco";

    private const string EbootName = "EBOOT.PBP";
    private const string CachedPrxName = "visualizer_plugin.prx";
    private const string CachedRcoName = "visualizer_plugin.rco";
    private const string ManifestName = "manifest.txt";
    private const int CacheFormat = 1;

    private readonly Dictionary<string, PafSurface> _textures;
    private readonly byte[] _cloudJpeg;
    private readonly byte[] _sandJpeg;

    private PspFirmwareAssets(Dictionary<string, PafSurface> textures, byte[] cloudJpeg, byte[] sandJpeg,
        string? sourcePath, string firmwareVersion, bool fromCache)
    {
        _textures = textures;
        _cloudJpeg = cloudJpeg;
        _sandJpeg = sandJpeg;
        SourcePath = sourcePath;
        FirmwareVersion = firmwareVersion;
        LoadedFromCache = fromCache;
    }

    /// <summary>The EBOOT.PBP the assets came from (null for <see cref="FromExtractedFiles"/>).</summary>
    public string? SourcePath { get; }

    /// <summary>Firmware version reported by the update, e.g. "6.61" (empty if unknown).</summary>
    public string FirmwareVersion { get; }

    /// <summary>True when the files came from the cache and no decryption was needed.</summary>
    public bool LoadedFromCache { get; }

    /// <summary>Names of all textures found in visualizer_plugin.rco.</summary>
    public IReadOnlyCollection<string> TextureNames => _textures.Keys;

    /// <inheritdoc />
    public PafSurface? GetRcoTexture(string name)
    {
        if (name.StartsWith('/')) name = name[1..];
        return _textures.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> CloudPaletteJpeg => _cloudJpeg;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> SandTrailsJpeg => _sandJpeg;

    /// <summary>
    /// Finds an official PSP update EBOOT.PBP under <paramref name="romsDirectory"/> (recursively, name
    /// matched case-insensitively; with several, the first that yields the files wins) and extracts the
    /// assets, using <paramref name="cacheDirectory"/> (if not null) to avoid decrypting again. Never
    /// throws for expected failures (missing file, wrong file, unsupported firmware, I/O errors): those
    /// come back as <see cref="PspFirmwareLoadResult.Error"/>. Only cancellation throws.
    /// </summary>
    public static PspFirmwareLoadResult TryLoad(string romsDirectory, string? cacheDirectory, CancellationToken cancel = default)
    {
        List<string> candidates;
        try
        {
            if (string.IsNullOrWhiteSpace(romsDirectory) || !Directory.Exists(romsDirectory))
                return PspFirmwareLoadResult.Fail($"The ROMs folder \"{romsDirectory}\" doesn't exist.");
            candidates = FindEboots(romsDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PspFirmwareLoadResult.Fail($"Couldn't search \"{romsDirectory}\" for {EbootName}: {e.Message}");
        }

        if (candidates.Count == 0)
            return PspFirmwareLoadResult.Fail(
                $"No {EbootName} found in \"{romsDirectory}\". The PSP visualizers need an official PSP system " +
                $"software update (for example 6.61) {EbootName} placed in that folder.");

        if (cacheDirectory is not null)
        {
            var cached = TryLoadFromCache(cacheDirectory, candidates);
            if (cached is not null) return PspFirmwareLoadResult.Ok(cached);
        }

        var errors = new List<string>();
        foreach (var path in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var (prx, rco, version) = ExtractFromEboot(path, cancel);
                var assets = Build(prx, rco, path, version, fromCache: false);
                if (cacheDirectory is not null) TryWriteCache(cacheDirectory, path, version, prx, rco);
                return PspFirmwareLoadResult.Ok(assets);
            }
            catch (PspFirmwareException e)
            {
                errors.Add($"\"{path}\": {e.Message}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"\"{path}\": {e.Message}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A malformed user-supplied file must not take the caller down.
                errors.Add($"\"{path}\": unexpected error while extracting ({e.GetType().Name}: {e.Message}).");
            }
        }

        return PspFirmwareLoadResult.Fail(errors.Count == 1
            ? $"Couldn't use the PSP firmware update {errors[0]}"
            : $"None of the {EbootName} files found could be used:\n" + string.Join("\n", errors));
    }

    /// <summary>Like <see cref="TryLoad"/> but throws <see cref="PspFirmwareException"/> on failure.</summary>
    public static PspFirmwareAssets Load(string romsDirectory, string? cacheDirectory, CancellationToken cancel = default)
    {
        var result = TryLoad(romsDirectory, cacheDirectory, cancel);
        return result.Assets ?? throw new PspFirmwareException(result.Error!);
    }

    /// <summary>Builds the assets from an already decrypted visualizer_plugin.prx and its .rco.</summary>
    public static PspFirmwareAssets FromExtractedFiles(byte[] decryptedPrx, byte[] rco) =>
        Build(decryptedPrx, rco, null, "", fromCache: false);

    /// <summary>
    /// Decrypts an EBOOT.PBP's PSAR and returns the decrypted, decompressed visualizer_plugin.prx and the
    /// raw visualizer_plugin.rco. Throws <see cref="PspFirmwareException"/> on failure.
    /// </summary>
    public static (byte[] Prx, byte[] Rco, string FirmwareVersion) ExtractFromEboot(string ebootPath, CancellationToken cancel = default)
    {
        using var psar = PsarArchive.Open(ebootPath);
        var files = psar.Extract([PrxPath, RcoPath], cancel);
        if (!files.TryGetValue(PrxPath, out var prxRaw))
            throw new PspFirmwareException(
                $"it doesn't contain visualizer_plugin.prx (firmware {psar.FirmwareVersion}); is it a PSP system software update?");
        if (!files.TryGetValue(RcoPath, out var rcoRaw))
            throw new PspFirmwareException($"it doesn't contain visualizer_plugin.rco (firmware {psar.FirmwareVersion}).");

        return (DecryptModule(psar, prxRaw, "visualizer_plugin.prx"), DecryptModule(psar, rcoRaw, "visualizer_plugin.rco"),
            psar.FirmwareVersion);
    }

    /// <summary>The "~PSP" branch of pspDecryptPSAR: decrypt, then gunzip / un-RLZ if compressed.</summary>
    private static byte[] DecryptModule(PsarArchive psar, byte[] data, string name)
    {
        if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual("~PSP"u8)) return data;
        var plain = psar.DecryptPrx(data)
                    ?? throw new PspFirmwareException(
                        $"couldn't decrypt {name} (tag 0x{psar.LastPrxTag:X8}); firmware {psar.FirmwareVersion} may not be supported.");
        try
        {
            if (plain.Length > 2 && plain[0] == 0x1F && plain[1] == 0x8B) return Gunzip(plain);
            if (plain.Length > 4 && plain.AsSpan(0, 4).SequenceEqual("2RLZ"u8)) return Lzr.Decompress(plain.AsSpan(4), 8 * 1024 * 1024);
            if (plain.Length > 4 && (plain.AsSpan(0, 4).SequenceEqual("KL4E"u8) || plain.AsSpan(0, 4).SequenceEqual("KL3E"u8)))
                throw new PspFirmwareException($"{name} is KL4E/KL3E-compressed, which isn't supported.");
            return plain;
        }
        catch (InvalidDataException e)
        {
            throw new PspFirmwareException($"{name} decrypted but couldn't be decompressed: {e.Message}", e);
        }
    }

    /// <summary>gunzip: skips the RFC 1952 header by hand, then inflates the single member (any trailing data is ignored).</summary>
    private static byte[] Gunzip(byte[] d)
    {
        if (d.Length < 18 || d[2] != 8) throw new InvalidDataException("bad gzip header.");
        int flags = d[3];
        int p = 10;
        if ((flags & 4) != 0) p += 2 + (d[p] | d[p + 1] << 8); // FEXTRA
        if ((flags & 8) != 0) p = Array.IndexOf(d, (byte)0, p) + 1; // FNAME
        if ((flags & 16) != 0) p = Array.IndexOf(d, (byte)0, p) + 1; // FCOMMENT
        if ((flags & 2) != 0) p += 2; // FHCRC
        if (p <= 0 || p >= d.Length) throw new InvalidDataException("bad gzip header.");
        using var z = new DeflateStream(new MemoryStream(d, p, d.Length - p, writable: false), CompressionMode.Decompress);
        using var ms = new MemoryStream(d.Length * 3);
        z.CopyTo(ms);
        return ms.ToArray();
    }

    private static PspFirmwareAssets Build(byte[] prx, byte[] rco, string? source, string version, bool fromCache)
    {
        if (prx.Length < 4 || !prx.AsSpan(0, 4).SequenceEqual("\u007FELF"u8))
            throw new PspFirmwareException("the decrypted visualizer_plugin.prx isn't an ELF module.");

        var cloud = PrxJpegs.Find(prx, PrxJpegs.CloudAddress, PrxJpegs.CloudLength, PrxJpegs.CloudWidth, PrxJpegs.CloudHeight)
                    ?? throw new PspFirmwareException("visualizer_plugin.prx doesn't contain the expected 64x64 cloud palette JPEG.");
        var sand = PrxJpegs.Find(prx, PrxJpegs.SandAddress, PrxJpegs.SandLength, PrxJpegs.SandWidth, PrxJpegs.SandHeight)
                   ?? throw new PspFirmwareException("visualizer_plugin.prx doesn't contain the expected 120x68 JPEG.");

        Dictionary<string, byte[]> gims;
        try
        {
            gims = RcoFile.ReadGimImages(rco);
        }
        catch (InvalidDataException e)
        {
            throw new PspFirmwareException($"visualizer_plugin.rco couldn't be read: {e.Message}", e);
        }
        if (gims.Count == 0) throw new PspFirmwareException("visualizer_plugin.rco contains no textures.");

        var textures = new Dictionary<string, PafSurface>(StringComparer.Ordinal);
        foreach (var (name, gim) in gims)
        {
            PafImage img;
            try
            {
                img = GimDecoder.Decode(gim);
            }
            catch (InvalidDataException e)
            {
                throw new PspFirmwareException($"texture \"{name}\" in visualizer_plugin.rco couldn't be decoded: {e.Message}", e);
            }
            textures[name] = new PafSurface(img.Width, img.Height, PafSurface.Mode8888, img.Pixels) { RcoName = name };
        }

        return new PspFirmwareAssets(textures, cloud, sand, source, version, fromCache);
    }

    private static List<string> FindEboots(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        var list = Directory.EnumerateFiles(root, EbootName, options)
            .Where(p => string.Equals(Path.GetFileName(p), EbootName, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ---------------------------------------------------------------- cache

    private static PspFirmwareAssets? TryLoadFromCache(string cacheDirectory, List<string> candidates)
    {
        try
        {
            string manifestPath = Path.Combine(cacheDirectory, ManifestName);
            if (!File.Exists(manifestPath)) return null;
            var m = File.ReadAllLines(manifestPath)
                .Select(l => l.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
            if (m.GetValueOrDefault("format") != CacheFormat.ToString(CultureInfo.InvariantCulture)) return null;
            string? source = m.GetValueOrDefault("source");
            string? match = candidates.FirstOrDefault(c => string.Equals(c, source, StringComparison.OrdinalIgnoreCase));
            if (match is null || m.GetValueOrDefault("stamp") != SourceStamp(match)) return null;

            byte[] prx = File.ReadAllBytes(Path.Combine(cacheDirectory, CachedPrxName));
            byte[] rco = File.ReadAllBytes(Path.Combine(cacheDirectory, CachedRcoName));
            if (m.GetValueOrDefault("prx.sha256") != Sha256(prx) || m.GetValueOrDefault("rco.sha256") != Sha256(rco)) return null;
            return Build(prx, rco, match, m.GetValueOrDefault("version") ?? "", fromCache: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null; // any problem with the cache just means extracting again
        }
    }

    private static void TryWriteCache(string cacheDirectory, string source, string version, byte[] prx, byte[] rco)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            WriteAtomic(Path.Combine(cacheDirectory, CachedPrxName), prx);
            WriteAtomic(Path.Combine(cacheDirectory, CachedRcoName), rco);
            var manifest = new StringBuilder()
                .Append("format=").Append(CacheFormat.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("source=").Append(source).Append('\n')
                .Append("stamp=").Append(SourceStamp(source)).Append('\n')
                .Append("version=").Append(version).Append('\n')
                .Append("prx.sha256=").Append(Sha256(prx)).Append('\n')
                .Append("rco.sha256=").Append(Sha256(rco)).Append('\n');
            WriteAtomic(Path.Combine(cacheDirectory, ManifestName), Encoding.UTF8.GetBytes(manifest.ToString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The cache is an optimisation only.
        }
    }

    /// <summary>Identifies a particular EBOOT.PBP file version: its size and last write time.</summary>
    private static string SourceStamp(string path)
    {
        var info = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static void WriteAtomic(string path, byte[] data)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
