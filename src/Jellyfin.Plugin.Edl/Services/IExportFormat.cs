using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>
/// One export format that <see cref="ExportSelector"/> can select and the controller can serve.
/// EDL is the only format today; MCF adds a second implementation.
/// </summary>
public interface IExportFormat
{
    /// <summary>Gets the lowercase format name, used as both the export directory and the file extension.</summary>
    string Name { get; }

    /// <summary>Gets the response content type for a served file.</summary>
    string ContentType { get; }

    /// <summary>Validates a file's exact bytes. An invalid file is reported, never served and never skipped.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <returns>The validation result.</returns>
    EdlValidationResult Validate(byte[] content);
}
