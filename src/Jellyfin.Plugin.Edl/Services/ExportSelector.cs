using Jellyfin.Plugin.Edl.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>
/// Selects the export file a requester receives for one authorized media version and format.
/// A user's personal file wins. The default is consulted only when the personal path is absent,
/// so an invalid or unreadable personal file is reported instead of falling back. A server API key
/// never has a personal path inspected, so its response is the same whatever personal files exist.
/// </summary>
public sealed class ExportSelector
{
    private static readonly IReadOnlyList<EdlValidationDiagnostic> NotARegularFile =
        new[] { new EdlValidationDiagnostic(0, "The export path is not a regular file") };

    private readonly ILogger<ExportSelector> _logger;

    /// <summary>Initializes a new instance of the <see cref="ExportSelector"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public ExportSelector(ILogger<ExportSelector> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets a test seam that runs after the selected entry is checked and before its bytes are read.</summary>
    internal Action<string>? BeforeRead { get; init; }

    /// <summary>Selects and reads the export for <paramref name="media"/> in <paramref name="format"/>.</summary>
    /// <param name="media">The version the requester is authorized to read exports for.</param>
    /// <param name="format">The export format.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The selection, carrying the exact file bytes when one was found.</returns>
    public async Task<ExportSelection> SelectAsync(AuthorizedMedia media, IExportFormat format, CancellationToken cancellationToken)
    {
        ExportSelection selection = ExportSelection.None.Instance;
        if (media.Requester is Requester.Account account
            && ExportLayout.PersonalPath(media.MediaPath, ExportLayout.ExportId(account.UserId), format.Name) is { } personal)
        {
            selection = await InspectAsync(personal, ExportScope.Personal, format, cancellationToken).ConfigureAwait(false);
        }

        if (selection is ExportSelection.None && ExportLayout.DefaultPath(media.MediaPath, format.Name) is { } fallback)
        {
            selection = await InspectAsync(fallback, ExportScope.Default, format, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Selected {Outcome} {Format} for item {ItemId} version {MediaSourceId}", selection.Outcome, format.Name, media.ItemId, media.MediaSourceId);
        return selection;
    }

    private async Task<ExportSelection> InspectAsync(string path, ExportScope scope, IExportFormat format, CancellationToken cancellationToken)
    {
        byte[] content;
        try
        {
            (ExportEntry entry, SafeFileHandle? handle) = ExportFile.Open(path);
            if (handle is null)
            {
                return entry == ExportEntry.Absent ? ExportSelection.None.Instance : new ExportSelection.Invalid(scope, NotARegularFile);
            }

            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            BeforeRead?.Invoke(path);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            content = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot read {Scope} {Format} export {Path}", scope, format.Name, path);
            return new ExportSelection.Unreadable(scope);
        }

        EdlValidationResult validation = format.Validate(content);
        return validation.IsValid
            ? new ExportSelection.Found(scope, content)
            : new ExportSelection.Invalid(scope, validation.Diagnostics);
    }
}
