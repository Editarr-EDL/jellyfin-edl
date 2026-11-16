using System.Runtime.Versioning;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Writes one MU-01 selection case into a media directory as real files.
/// The media file is an empty placeholder. These are inputs for planned
/// personal and default selection; they encode no selection logic.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class SelectionCaseMaterializer
{
    /// <summary>Creates the case's entries under <paramref name="mediaDirectory"/> and returns the media path.</summary>
    public static string Materialize(SelectionCase selectionCase, string mediaDirectory)
    {
        Directory.CreateDirectory(mediaDirectory);
        string mediaPath = Path.Combine(mediaDirectory, selectionCase.Media);
        File.WriteAllBytes(mediaPath, Array.Empty<byte>());

        foreach (SelectionEntry entry in selectionCase.Entries)
        {
            string path = PathOf(mediaDirectory, entry);
            if (entry.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ExportLayoutFixture.PayloadBytes(entry.Payload!));
            if (entry.Unreadable)
            {
                File.SetUnixFileMode(path, UnixFileMode.None);
            }
        }

        return mediaPath;
    }

    public static string PathOf(string mediaDirectory, SelectionEntry entry) =>
        Path.Combine(mediaDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
}
