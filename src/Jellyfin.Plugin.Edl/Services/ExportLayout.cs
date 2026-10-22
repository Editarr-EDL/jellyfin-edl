using System.Globalization;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>
/// Export paths of the editarr-export-layout-1 contract. For <c>Movie.mkv</c> and format <c>edl</c>
/// the personal file is <c>edl/&lt;id&gt;/Movie.user-&lt;id&gt;.edl</c> and the default is
/// <c>edl/Movie.edl</c>, both beside the media. The adjacent legacy <c>Movie.edl</c> has no path here.
/// </summary>
public static class ExportLayout
{
    /// <summary>Returns the export ID of a Jellyfin user: 32 lowercase hex digits.</summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The canonical export ID.</returns>
    public static string ExportId(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the media file name without its final extension, or null for a name the contract
    /// refuses: empty, a leading dot as the only dot (<c>.mkv</c>), or an empty extension (<c>Movie.</c>).
    /// </summary>
    /// <param name="fileName">The media file name.</param>
    /// <returns>The stem, or null.</returns>
    public static string? MediaStem(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0)
        {
            return fileName.Length == 0 ? null : fileName;
        }

        return dot == 0 || dot == fileName.Length - 1 ? null : fileName[..dot];
    }

    /// <summary>Computes a user's personal export path for one media file and format.</summary>
    /// <param name="mediaPath">The rooted media path.</param>
    /// <param name="exportId">The user's export ID.</param>
    /// <param name="format">The format name.</param>
    /// <returns>The path, or null when the media name has no supported stem.</returns>
    public static string? PersonalPath(string mediaPath, string exportId, string format) =>
        Split(mediaPath) is { } media ? Path.Combine(media.Directory, format, exportId, $"{media.Stem}.user-{exportId}.{format}") : null;

    /// <summary>Computes the default export path for one media file and format.</summary>
    /// <param name="mediaPath">The rooted media path.</param>
    /// <param name="format">The format name.</param>
    /// <returns>The path, or null when the media name has no supported stem.</returns>
    public static string? DefaultPath(string mediaPath, string format) =>
        Split(mediaPath) is { } media ? Path.Combine(media.Directory, format, $"{media.Stem}.{format}") : null;

    private static (string Directory, string Stem)? Split(string mediaPath)
    {
        string? directory = Path.GetDirectoryName(mediaPath);
        string? stem = MediaStem(Path.GetFileName(mediaPath));
        return directory is null || stem is null ? null : (directory, stem);
    }
}
