using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

internal sealed record GrammarVector(string Name, string Content, bool Valid);

internal sealed record Payload(string File, string Format, bool Valid, string Sha256);

internal sealed record SelectionEntry(string Path, string? Payload, bool Directory, bool Unreadable);

internal sealed record SelectionExpectation(string Outcome, string? Payload);

internal sealed record SelectionCase(
    string Name,
    string Media,
    string Principal,
    string Format,
    IReadOnlyList<SelectionEntry> Entries,
    SelectionExpectation Expect);

internal sealed record ExportLayoutManifest(
    string Contract,
    IReadOnlyList<GrammarVector> GrammarVectors,
    IReadOnlyDictionary<string, Payload> Payloads,
    IReadOnlyList<SelectionCase> Selection);

/// <summary>
/// Loads the Editarr export-layout-v1 contract copied next to the test assembly.
/// </summary>
internal static class ExportLayoutFixture
{
    public const string PinnedManifestSha256 = "c3063858e3ecf2f8982c66a1140f52ab7d806e737ff5c7854b413cdecccf987e";

    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures", "export-layout-v1");

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<ExportLayoutManifest> Loaded = new(Load);

    public static ExportLayoutManifest Manifest => Loaded.Value;

    public static string Sha256Of(string relativePath) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, relativePath))));

    public static byte[] PayloadBytes(string payloadId) =>
        File.ReadAllBytes(Path.Combine(Root, Manifest.Payloads[payloadId].File));

    public static SelectionCase Case(string name) => Manifest.Selection.Single(c => c.Name == name);

    private static ExportLayoutManifest Load()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "manifest.json")));
        JsonElement root = document.RootElement;
        return new ExportLayoutManifest(
            root.GetProperty("contract").GetString()!,
            Read<List<GrammarVector>>(root.GetProperty("edlGrammar").GetProperty("vectors")),
            Read<Dictionary<string, Payload>>(root.GetProperty("payloads")),
            Read<List<SelectionCase>>(root.GetProperty("selection")));
    }

    private static T Read<T>(JsonElement element) => element.Deserialize<T>(Options)!;
}
