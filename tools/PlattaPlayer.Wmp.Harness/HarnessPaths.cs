namespace PlattaPlayer.Wmp.Harness;

/// <summary>
/// Where the harness keeps its artefacts. Everything lands under a single <c>artifacts\</c> folder
/// beside the project so a run leaves nothing scattered and the whole lot can be deleted at will.
/// </summary>
internal static class HarnessPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string SynthFile => Path.Combine(Root, "synth", "frames.bin");
    public static string TruthDir => Path.Combine(Root, "truth");
    public static string OursDir => Path.Combine(Root, "ours");
    public static string ReportDir => Path.Combine(Root, "report");
    public static string DumpDir => Path.Combine(Root, "dump");

    /// <summary>
    /// Frame set for one capture. <paramref name="freshState"/> is part of the name because sending
    /// fresh frames as state 3 instead of 2 suppresses wmp.dll's bar jitter, and the two runs are not
    /// interchangeable.
    /// </summary>
    public static string FrameSetFile(string baseDir, string effect, int preset, int w, int h, int freshState = 2)
        => Path.Combine(baseDir, freshState == 2
            ? $"{effect}-p{preset}-{w}x{h}.bin"
            : $"{effect}-p{preset}-{w}x{h}-s{freshState}.bin");

    private static string ResolveRoot()
    {
        // Walk up from the binary to the project folder so `dotnet run` and a direct exe launch agree.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "PlattaPlayer.Wmp.Harness.csproj")))
                return Path.Combine(dir, "artifacts");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts");
    }
}
