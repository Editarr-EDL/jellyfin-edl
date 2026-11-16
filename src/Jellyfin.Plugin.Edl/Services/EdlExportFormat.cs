using System.Text;
using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>The EDL export format.</summary>
public sealed class EdlExportFormat : IExportFormat
{
    private readonly IEdlParser _parser;

    /// <summary>Initializes a new instance of the <see cref="EdlExportFormat"/> class.</summary>
    /// <param name="parser">The EDL line validator.</param>
    public EdlExportFormat(IEdlParser parser)
    {
        _parser = parser;
    }

    /// <inheritdoc />
    public string Name => "edl";

    /// <inheritdoc />
    public string ContentType => "text/plain; charset=utf-8";

    /// <inheritdoc />
    public ExportValidationResult Validate(byte[] content)
    {
        if (content.Length > ExportFile.MaxContentBytes)
        {
            return ExportValidationResult.Invalid([new ExportValidationDiagnostic(0, "EDL content exceeds the size limit")]);
        }

        // The export-layout contract pins validity to File.ReadAllLines, which decodes exactly like this reader.
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            lines.Add(line);
        }

        return _parser.Validate(lines.ToArray());
    }
}
