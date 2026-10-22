namespace Jellyfin.Plugin.Edl.Models;

public sealed record ExportValidationDiagnostic(int LineNumber, string Message);

public sealed record ExportValidationResult(bool IsValid, IReadOnlyList<ExportValidationDiagnostic> Diagnostics)
{
    public static readonly ExportValidationResult Valid = new(true, Array.Empty<ExportValidationDiagnostic>());

    public static ExportValidationResult Invalid(IEnumerable<ExportValidationDiagnostic> diagnostics) =>
        new(false, diagnostics.ToArray());
}

public sealed record ExportValidationErrorResponse(IReadOnlyList<ExportValidationDiagnostic> Diagnostics);
