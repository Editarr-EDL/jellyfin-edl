using System.Text;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Serves MU-01 EDL grammar vectors and EDL payloads as a default export through the real request path.
/// A valid file comes back byte for byte; an invalid one is 422.
/// </summary>
public sealed class EdlGrammarVectorTests
{
    public static TheoryData<string> VectorNames() =>
        new(ExportLayoutFixture.Manifest.GrammarVectors.Select(v => v.Name));

    public static TheoryData<string> EdlPayloadIds() =>
        new(ExportLayoutFixture.Manifest.Payloads.Where(p => p.Value.Format == "edl").Select(p => p.Key));

    [Theory]
    [MemberData(nameof(VectorNames))]
    public async Task Plugin_agrees_with_the_editarr_grammar_vector(string name)
    {
        GrammarVector vector = ExportLayoutFixture.Manifest.GrammarVectors.Single(v => v.Name == name);

        await AssertServedAsync(vector.Valid, new UTF8Encoding(false).GetBytes(vector.Content));
    }

    [Theory]
    [MemberData(nameof(EdlPayloadIds))]
    public async Task Plugin_agrees_with_the_editarr_payload_validity(string payloadId)
    {
        await AssertServedAsync(ExportLayoutFixture.Manifest.Payloads[payloadId].Valid, ExportLayoutFixture.PayloadBytes(payloadId));
    }

    private static async Task AssertServedAsync(bool valid, byte[] content)
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "edl"));
        File.WriteAllBytes(Path.Combine(directory.Path, "edl", "Movie.edl"), content);
        var plugin = new PluginHarness();
        var user = PluginHarness.NewUser(Guid.NewGuid());
        Guid item = plugin.AddVideo(new[] { user }, PluginHarness.Source(Guid.NewGuid(), Path.Combine(directory.Path, "Movie.mkv")));

        PluginResponse response = await plugin.GetAsync(user, item);

        if (valid)
        {
            Assert.Equal(200, response.Status);
            Assert.Equal(content, response.Body);
        }
        else
        {
            Assert.Equal(422, response.Status);
        }
    }
}
