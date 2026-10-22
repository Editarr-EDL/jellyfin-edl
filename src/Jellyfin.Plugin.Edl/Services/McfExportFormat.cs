using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>The MovieContentFilter (MCF) export format, versions 1.0.0 and 1.1.0.</summary>
public sealed class McfExportFormat : IExportFormat
{
    /// <inheritdoc />
    public string Name => "mcf";

    /// <summary>Gets the WebVTT media type, because an MCF document is a WebVTT file.</summary>
    public string ContentType => "text/vtt; charset=utf-8";

    /// <inheritdoc />
    public ExportValidationResult Validate(byte[] content) => McfValidator.Validate(content);
}
