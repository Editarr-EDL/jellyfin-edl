using System.Text.Json;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Pins the imported fixture to the exact Editarr export-layout-v1 bytes.
/// </summary>
public sealed class ExportLayoutFixtureTests
{
    public static TheoryData<string> PayloadIds() => new(ExportLayoutFixture.Manifest.Payloads.Keys);

    [Fact]
    public void Manifest_matches_the_pinned_editarr_contract()
    {
        Assert.Equal(ExportLayoutFixture.PinnedManifestSha256, ExportLayoutFixture.Sha256Of("manifest.json"));
        Assert.Equal("editarr-export-layout-1", ExportLayoutFixture.Manifest.Contract);
        Assert.Equal(20, ExportLayoutFixture.Manifest.GrammarVectors.Count);
        Assert.Equal(13, ExportLayoutFixture.Manifest.Payloads.Count);
        Assert.Equal(19, ExportLayoutFixture.Manifest.Selection.Count);
    }

    [Fact]
    public void Provenance_records_the_same_manifest_hash()
    {
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ExportLayoutFixture.Root, "provenance.json")));

        Assert.Equal(ExportLayoutFixture.PinnedManifestSha256, provenance.RootElement.GetProperty("manifestSha256").GetString());
    }

    [Theory]
    [MemberData(nameof(PayloadIds))]
    public void Payload_matches_its_manifest_digest(string payloadId)
    {
        Payload payload = ExportLayoutFixture.Manifest.Payloads[payloadId];

        Assert.Equal(payload.Sha256, ExportLayoutFixture.Sha256Of(payload.File));
    }
}
