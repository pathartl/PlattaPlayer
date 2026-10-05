using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Psf;

namespace PlattaPlayer.Codecs.Usf.Psf;

/// <summary>
/// The USF-specific PSF tags: the ripper tag ("usfby") and the player flags lazyusf2 needs. The rest of the
/// mapping is the PSF family's (<see cref="PsfTagMap"/>).
/// </summary>
internal static class UsfTags
{
    public const string UsfBy = "usfby";

    public const string EnableCompare = "_enablecompare";
    public const string EnableFifoFull = "_enablefifofull";

    /// <summary>The library's view of a file's tags (keys as in <see cref="UsfCodecPlugin"/>'s fields).</summary>
    public static CodecTags ToCodecTags(PsfTags psf) => PsfTagMap.ToCodecTags(psf, UsfBy);

    /// <summary>Applies the edited <paramref name="tags"/>, as <see cref="PsfTagMap.Apply"/>.</summary>
    public static void Apply(PsfTags psf, CodecTags tags) => PsfTagMap.Apply(psf, tags, UsfBy);
}
