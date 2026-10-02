namespace PlattaPlayer.Codecs.Usf.Psf;

/// <summary>
/// Everything the emulator is loaded with for one song: the reserved areas of the file and of the libraries
/// it names, in upload order. A miniUSF holds only a few patched bytes and names a .usflib (with "_lib")
/// carrying the game's sound code; a library may name libraries of its own.
/// <para>The order is psflib's, which lazyusf2 expects: a file's "_lib" (and, recursively, its libraries)
/// first, then the file itself, then its "_lib2", "_lib3"… Library paths are relative to the file naming
/// them.</para>
/// </summary>
internal sealed class UsfSet
{
    // psflib's limit, which stops a library that (indirectly) names itself.
    private const int MaxDepth = 10;

    private UsfSet(List<byte[]> sections, bool enableCompare, bool enableFifoFull)
    {
        Sections = sections;
        EnableCompare = enableCompare;
        EnableFifoFull = enableFifoFull;
    }

    public IReadOnlyList<byte[]> Sections { get; }

    /// <summary>"_enablecompare" is set in any of the files: the rip needs the COMPARE interrupt.</summary>
    public bool EnableCompare { get; }

    /// <summary>"_enablefifofull" is set in any of the files: the rip needs the AI FIFO-full behaviour.</summary>
    public bool EnableFifoFull { get; }

    /// <summary>Loads <paramref name="path"/> and its libraries. Throws when a file is missing or invalid.</summary>
    public static UsfSet Load(string path)
    {
        var sections = new List<byte[]>();
        var compare = false;
        var fifoFull = false;
        Add(Path.GetFullPath(path), 0);
        return new UsfSet(sections, compare, fifoFull);

        void Add(string file, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException($"{Path.GetFileName(path)}: its libraries are nested too deeply.");
            if (!File.Exists(file)) throw new FileNotFoundException($"Missing USF library {Path.GetFileName(file)}.", file);
            var psf = PsfFile.TryLoad(file) ?? throw new InvalidDataException($"{Path.GetFileName(file)} is not a USF file.");

            compare |= !string.IsNullOrEmpty(psf.Tags[UsfTags.EnableCompare]);
            fifoFull |= !string.IsNullOrEmpty(psf.Tags[UsfTags.EnableFifoFull]);

            var folder = Path.GetDirectoryName(file) ?? "";
            if (psf.Tags["_lib"] is { } lib) Add(Path.GetFullPath(lib, folder), depth + 1);
            if (psf.Reserved.Length > 0) sections.Add(psf.Reserved.ToArray());
            for (var n = 2; psf.Tags[$"_lib{n}"] is { } numbered; n++)
                Add(Path.GetFullPath(numbered, folder), depth + 1);
        }
    }
}
