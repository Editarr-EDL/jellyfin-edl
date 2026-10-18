namespace Jellyfin.Plugin.Edl.Models;

public sealed record EdlValidationDiagnostic(int LineNumber, string Message);

public sealed record EdlValidationResult(bool IsValid, IReadOnlyList<EdlValidationDiagnostic> Diagnostics)
{
    public static readonly EdlValidationResult Valid = new(true, Array.Empty<EdlValidationDiagnostic>());

    public static EdlValidationResult Invalid(IEnumerable<EdlValidationDiagnostic> diagnostics) =>
        new(false, diagnostics.ToArray());
}

public sealed record EdlValidationErrorResponse(IReadOnlyList<EdlValidationDiagnostic> Diagnostics);
