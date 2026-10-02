namespace PlattaPlayer.Sources.Local;

/// <summary>
/// Folder art by convention: the image named cover.* or folder.* beside the music. Used for formats that
/// carry no art of their own (codec formats such as SPC, or MIDI without a cover of its own).
/// </summary>
public static class FolderCover
{
    // Lookup order: the first that exists wins.
    private static readonly string[] CoverFileNames =
    {
        "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.jpeg", "folder.png"
    };

    /// <summary>The folder's cover image, or null.</summary>
    public static string? Find(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return null;
        foreach (var name in CoverFileNames)
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    /// <summary>
    /// Makes <paramref name="image"/> the folder's cover: copies it to <c>cover.&lt;ext&gt;</c> and removes the
    /// other <c>cover.*</c> images that <see cref="Find"/> would otherwise still pick first. Returns the path.
    /// </summary>
    public static string Install(string image, string dir)
    {
        var ext = Path.GetExtension(image).ToLowerInvariant();
        var dest = Path.Combine(dir, "cover" + ext);
        if (!string.Equals(Path.GetFullPath(image), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(image, dest, overwrite: true);

        foreach (var name in CoverFileNames)
        {
            if (!name.StartsWith("cover.", StringComparison.Ordinal)) continue;
            var other = Path.Combine(dir, name);
            if (!string.Equals(other, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(other))
                File.Delete(other);
        }
        return dest;
    }
}
