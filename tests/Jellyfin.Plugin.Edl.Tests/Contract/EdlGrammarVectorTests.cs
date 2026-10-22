using System.Text;
using Jellyfin.Plugin.Edl.Api;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Runs MU-01 EDL grammar vectors and EDL payloads through the controller's real file read and parser.
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
        using var directory = new TempDirectory();
        string path = Path.Combine(directory.Path, "vector.edl");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(vector.Content));

        AssertValidity(vector.Valid, await ServeAsync(path));
    }

    [Theory]
    [MemberData(nameof(EdlPayloadIds))]
    public async Task Plugin_agrees_with_the_editarr_payload_validity(string payloadId)
    {
        Payload payload = ExportLayoutFixture.Manifest.Payloads[payloadId];

        AssertValidity(payload.Valid, await ServeAsync(Path.Combine(ExportLayoutFixture.Root, payload.File)));
    }

    private static void AssertValidity(bool valid, IActionResult result)
    {
        if (valid)
        {
            Assert.IsType<ContentResult>(result);
        }
        else
        {
            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }
    }

    private static Task<IActionResult> ServeAsync(string edlPath)
    {
        var lookup = new Mock<IEdlLookupService>();
        lookup.Setup(x => x.ResolveByItemId(It.IsAny<Guid>()))
            .Returns(EdlLookupResult.Found("/media/Movie.mkv", edlPath));
        var controller = new EdlController(lookup.Object, new EdlParser(), NullLogger<EdlController>.Instance);
        return controller.GetByItemId(Guid.NewGuid(), CancellationToken.None);
    }
}
