using PlattaPlayer.Codecs.Psf;

namespace PlattaPlayer.Codecs.Usf.Psf;

/// <summary>
/// Everything the emulator is loaded with for one song: the reserved areas of the file and of the libraries
/// it names, in psflib's upload order (<see cref="PsfSet"/>), which lazyusf2 expects. Files with an empty
/// reserved area add nothing.
/// </summary>
internal sealed class UsfSet
{
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
        var files = PsfSet.Load(path, PsfFile.UsfVersion, "USF");
        return new UsfSet(
            files.Where(f => f.Reserved.Length > 0).Select(f => f.Reserved.ToArray()).ToList(),
            files.Any(f => !string.IsNullOrEmpty(f.Tags[UsfTags.EnableCompare])),
            files.Any(f => !string.IsNullOrEmpty(f.Tags[UsfTags.EnableFifoFull])));
    }
}
