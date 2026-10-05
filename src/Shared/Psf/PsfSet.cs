namespace PlattaPlayer.Codecs.Psf;

/// <summary>
/// A song and the libraries it names, in load order. A "mini" file (miniusf, minigsf…) holds only a few
/// patched bytes and names a library (with "_lib") carrying the game's sound code; a library may name
/// libraries of its own.
/// <para>The order is psflib's, which every PSF player follows: a file's "_lib" (and, recursively, its
/// libraries) first, then the file itself, then its "_lib2", "_lib3"… Each file is loaded over what came
/// before. Library paths are relative to the file naming them.</para>
/// </summary>
internal static class PsfSet
{
    // psflib's limit, which stops a library that (indirectly) names itself.
    private const int MaxDepth = 10;

    /// <summary>Loads <paramref name="path"/> and its libraries (all PSFs of <paramref name="version"/>; the
    /// <paramref name="formatName"/> is for messages). Throws when a file is missing or invalid.</summary>
    public static IReadOnlyList<PsfFile> Load(string path, byte version, string formatName)
    {
        var files = new List<PsfFile>();
        Add(Path.GetFullPath(path), 0);
        return files;

        void Add(string file, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException($"{Path.GetFileName(path)}: its libraries are nested too deeply.");
            if (!File.Exists(file)) throw new FileNotFoundException($"Missing {formatName} library {Path.GetFileName(file)}.", file);
            var psf = PsfFile.TryLoad(file, version) ?? throw new InvalidDataException($"{Path.GetFileName(file)} is not a {formatName} file.");

            var folder = Path.GetDirectoryName(file) ?? "";
            if (psf.Tags["_lib"] is { } lib) Add(Path.GetFullPath(lib, folder), depth + 1);
            files.Add(psf);
            for (var n = 2; psf.Tags[$"_lib{n}"] is { } numbered; n++)
                Add(Path.GetFullPath(numbered, folder), depth + 1);
        }
    }
}
