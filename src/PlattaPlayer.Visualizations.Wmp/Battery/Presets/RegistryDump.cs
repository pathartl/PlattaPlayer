using System.Globalization;
using System.Reflection;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Presets;

/// <summary>One registry key from the embedded dump: its values and its subkeys, in enumeration order.</summary>
public sealed class RegistryKeyData
{
    private readonly List<RegistryKeyData> _subKeys = [];
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public RegistryKeyData(string name) => Name = name;

    public string Name { get; }

    /// <summary>Subkeys in the order <c>RegEnumKeyEx</c> returned them, which is the order the real
    /// loader assigns preset indices in.</summary>
    public IReadOnlyList<RegistryKeyData> SubKeys => _subKeys;

    public RegistryKeyData? SubKey(string name) =>
        _subKeys.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>REG_SZ → string, REG_DWORD → uint, REG_BINARY → byte[]; null when absent.</summary>
    public object? Value(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public string? String(string name) => Value(name) as string;

    public uint? DWord(string name) => Value(name) as uint?;

    public byte[]? Binary(string name) => Value(name) as byte[];

    internal void AddSubKey(RegistryKeyData key) => _subKeys.Add(key);

    internal void SetValue(string name, object value) => _values[name] = value;
}

/// <summary>
/// Reads the <c>reg query /s</c> text the Battery presets are embedded as.
///
/// The recipes are kept as WMP's own registry data rather than re-encoded, so nothing about them is a
/// transcription: REG_SZ values stay strings and are parsed later by the same rules the real loader
/// uses. The format is one key path per line, then one <c>    name    TYPE    data</c> line per value,
/// with four-space separators. A value can have a space in its name or data, but never a run of four.
/// </summary>
public static class RegistryDump
{
    private const string ResourceName = "PlattaPlayer.Visualizations.Wmp.Battery.Presets.BatteryPresets.reg.txt";

    private static RegistryKeyData? _embedded;

    /// <summary>The <c>Battery</c> key of the embedded dump.</summary>
    public static RegistryKeyData Embedded => _embedded ??= LoadEmbedded();

    private static RegistryKeyData LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static RegistryKeyData Parse(string text)
    {
        var keys = new Dictionary<string, RegistryKeyData>(StringComparer.OrdinalIgnoreCase);
        RegistryKeyData? root = null;
        RegistryKeyData? current = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(';')) continue;

            if (!line.StartsWith("    ", StringComparison.Ordinal))
            {
                current = GetOrCreate(line.Trim());
                continue;
            }

            if (current is null) throw new FormatException("Value line before any key.");
            var parts = line.Trim().Split("    ", 3, StringSplitOptions.None);
            if (parts.Length < 2) throw new FormatException($"Bad value line '{line}'.");
            var data = parts.Length > 2 ? parts[2] : "";
            current.SetValue(parts[0], parts[1] switch
            {
                "REG_SZ" => data,
                "REG_DWORD" => uint.Parse(data.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                "REG_BINARY" => Convert.FromHexString(data),
                _ => throw new FormatException($"Unsupported registry type {parts[1]}."),
            });
        }

        return root ?? throw new FormatException("Empty registry dump.");

        // `reg query /s` prints the key it was asked for only when it holds values, so a parent can be
        // implied by its first child's path.
        RegistryKeyData GetOrCreate(string path)
        {
            if (keys.TryGetValue(path, out var existing)) return existing;
            var slash = path.LastIndexOf('\\');
            var key = new RegistryKeyData(slash < 0 ? path : path[(slash + 1)..]);
            if (slash < 0) root ??= key;
            else GetOrCreate(path[..slash]).AddSubKey(key);
            keys[path] = key;
            return key;
        }
    }
}
