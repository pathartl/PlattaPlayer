using PlattaPlayer.Codecs.AlbumFiles;

namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// MIDI carries no embedded art, so cover lookup is sidecar-based: an explicit <c>cover=</c> tag (a path
/// relative to the MIDI file), then the <c>#EXTIMG:</c> of an M3U sidecar listing the file. Without either the
/// host falls back to the folder's own image (cover.jpg, folder.jpg…).
/// </summary>
internal static class MidiCoverResolver
{
    public static string? Resolve(string midiPath, M3uSidecarIndex m3u)
    {
        try
        {
            var dir = Path.GetDirectoryName(midiPath);
            if (string.IsNullOrEmpty(dir))
                return null;

            if (MidiTags.Read(midiPath).Cover is { } rel)
            {
                var coverPath = Path.GetFullPath(Path.Combine(dir, rel));
                // Keep the reference inside the MIDI's own folder subtree (it comes from file content).
                var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                if (coverPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(coverPath))
                    return coverPath;
            }

            if (m3u.Find(midiPath)?.CoverPath is { } playlistCover && File.Exists(playlistCover))
                return playlistCover;
        }
        catch
        {
            // Unreadable sidecar — treat as no cover rather than failing.
        }

        return null;
    }
}
