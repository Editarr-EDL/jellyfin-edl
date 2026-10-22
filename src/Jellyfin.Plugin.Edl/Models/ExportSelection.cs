namespace Jellyfin.Plugin.Edl.Models;

/// <summary>Which export file a selection came from.</summary>
public enum ExportScope
{
    /// <summary><c>&lt;format&gt;/&lt;user id&gt;/&lt;stem&gt;.user-&lt;user id&gt;.&lt;format&gt;</c> beside the media.</summary>
    Personal,

    /// <summary><c>&lt;format&gt;/&lt;stem&gt;.&lt;format&gt;</c> beside the media.</summary>
    Default,
}

/// <summary>
/// The result of selecting one format's export for an authorized media version.
/// <see cref="Outcome"/> uses the outcome names of the editarr-export-layout-1 contract.
/// </summary>
public abstract record ExportSelection
{
    private ExportSelection()
    {
    }

    /// <summary>Gets the contract outcome name, such as <c>personal</c> or <c>default_invalid</c>.</summary>
    public abstract string Outcome { get; }

    /// <summary>Neither the personal nor the default file exists.</summary>
    public sealed record None : ExportSelection
    {
        /// <summary>Gets the only instance.</summary>
        public static readonly None Instance = new();

        /// <inheritdoc />
        public override string Outcome => "none";
    }

    /// <summary>A valid file, served as its exact bytes.</summary>
    /// <param name="Scope">The file that was selected.</param>
    /// <param name="Content">The file's bytes.</param>
    public sealed record Found(ExportScope Scope, byte[] Content) : ExportSelection
    {
        /// <inheritdoc />
        public override string Outcome => Scope == ExportScope.Personal ? "personal" : "default";
    }

    /// <summary>The selected path is not a regular file, or its content failed validation.</summary>
    /// <param name="Scope">The file that was selected.</param>
    /// <param name="Diagnostics">Why it is invalid.</param>
    public sealed record Invalid(ExportScope Scope, IReadOnlyList<ExportValidationDiagnostic> Diagnostics) : ExportSelection
    {
        /// <inheritdoc />
        public override string Outcome => Scope == ExportScope.Personal ? "personal_invalid" : "default_invalid";
    }

    /// <summary>The selected path exists but could not be inspected or read.</summary>
    /// <param name="Scope">The file that was selected.</param>
    public sealed record Unreadable(ExportScope Scope) : ExportSelection
    {
        /// <inheritdoc />
        public override string Outcome => Scope == ExportScope.Personal ? "personal_unreadable" : "default_unreadable";
    }
}
