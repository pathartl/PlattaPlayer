namespace PlattaPlayer.Codecs.Abstractions;

/// <summary>
/// The codec plugins available to the host, looked up by file extension. When two plugins claim the same
/// extension (or id) the one registered first wins, so built-in codecs can't be hijacked by drop-ins.
/// </summary>
public sealed class CodecRegistry
{
    private readonly Dictionary<string, ICodecPlugin> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public CodecRegistry(IEnumerable<ICodecPlugin> plugins)
    {
        var list = new List<ICodecPlugin>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            if (!ids.Add(plugin.Id)) continue;
            list.Add(plugin);
            foreach (var ext in plugin.Extensions)
                _byExtension.TryAdd(ext.StartsWith('.') ? ext : "." + ext, plugin);
        }
        Plugins = list;
    }

    /// <summary>An empty registry: every file goes to the built-in decoders.</summary>
    public static CodecRegistry Empty { get; } = new([]);

    public IReadOnlyList<ICodecPlugin> Plugins { get; }

    /// <summary>Every extension some codec handles, with the leading dot.</summary>
    public IReadOnlyCollection<string> Extensions => _byExtension.Keys;

    /// <summary>The codec for a file, by its extension, or null.</summary>
    public ICodecPlugin? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && _byExtension.TryGetValue(ext, out var plugin) ? plugin : null;
    }
}
