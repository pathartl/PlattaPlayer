namespace PlattaPlayer.Core.Models;

/// <summary>
/// Multi-value tag fields (artist, album artist, genre). A semicolon in a tag separates values, so
/// "Artist A; Artist B" is two artists. The library stores such a field as one string in the canonical
/// form "A; B" — values trimmed, blanks and case-insensitive duplicates dropped — and splits it again
/// wherever the individual values matter.
/// </summary>
public static class TagValues
{
    public const char Delimiter = ';';
    public const string Separator = "; ";

    /// <summary>The individual values of a field, in tag order.</summary>
    public static IReadOnlyList<string> Split(string? value) => Split([value]);

    /// <summary>The individual values of a field that a tag format may already store as several values,
    /// each of which may itself be semicolon-delimited.</summary>
    public static IReadOnlyList<string> Split(IEnumerable<string?>? values)
    {
        var result = new List<string>();
        if (values is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            foreach (var part in value.Split(Delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (seen.Add(part)) result.Add(part);
        }
        return result;
    }

    /// <summary>The canonical "A; B" form of a field, or null when it has no values.</summary>
    public static string? Normalize(string? value) => Join(Split(value));

    /// <summary>The canonical "A; B" form of a multi-value field, or null when it has no values.</summary>
    public static string? Normalize(IEnumerable<string?>? values) => Join(Split(values));

    /// <summary>Values for display, comma-separated ("Ambient, Dream pop").</summary>
    public static string Display(string? value) => string.Join(", ", Split(value));

    private static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join(Separator, values);
}
