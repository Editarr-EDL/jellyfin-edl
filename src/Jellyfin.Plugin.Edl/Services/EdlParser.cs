using System.Globalization;
using Jellyfin.Plugin.Edl.Models;

namespace Jellyfin.Plugin.Edl.Services;

public sealed class EdlParser : IEdlParser
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "0", "1", "2", "3", "cut", "mute", "scene", "commercial"
    };

    public ExportValidationResult Validate(string[] lines)
    {
        var diagnostics = new List<ExportValidationDiagnostic>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || IsCommentLine(line))
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                diagnostics.Add(new ExportValidationDiagnostic(i + 1, "Expected at least three fields: start end action"));
                continue;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double start))
            {
                diagnostics.Add(new ExportValidationDiagnostic(i + 1, "Invalid start time"));
                continue;
            }

            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
            {
                diagnostics.Add(new ExportValidationDiagnostic(i + 1, "Invalid end time"));
                continue;
            }

            if (end <= start)
            {
                diagnostics.Add(new ExportValidationDiagnostic(i + 1, "End time must be greater than start time"));
                continue;
            }

            string action = parts[2];
            if (!AllowedActions.Contains(action))
            {
                diagnostics.Add(new ExportValidationDiagnostic(i + 1, $"Unsupported action '{action}'"));
            }
        }

        return diagnostics.Count == 0
            ? ExportValidationResult.Valid
            : ExportValidationResult.Invalid(diagnostics);
    }

    private static bool IsCommentLine(string line)
    {
        return line.StartsWith("#", StringComparison.Ordinal)
            || line.StartsWith(";", StringComparison.Ordinal)
            || line.StartsWith("//", StringComparison.Ordinal);
    }
}
