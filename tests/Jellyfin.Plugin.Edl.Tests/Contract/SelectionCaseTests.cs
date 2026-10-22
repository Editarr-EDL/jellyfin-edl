using System.Runtime.Versioning;
using System.Text;
using Jellyfin.Plugin.Edl.Api;
using Jellyfin.Plugin.Edl.Services;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Materializes the 19 MU-01 selection cases as planned inputs and records what
/// the current legacy selector does with each EDL case. The contract outcomes
/// are not implemented here; MU-09 and MU-12 own them.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class SelectionCaseTests
{
    private readonly ITestOutputHelper _output;

    public SelectionCaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> AllCases() => new(ExportLayoutFixture.Manifest.Selection.Select(c => c.Name));

    public static TheoryData<string> EdlCases() =>
        new(ExportLayoutFixture.Manifest.Selection.Where(c => c.Format == "edl").Select(c => c.Name));

    [Fact]
    public void Cases_cover_every_planned_input_kind()
    {
        IReadOnlyList<SelectionCase> cases = ExportLayoutFixture.Manifest.Selection;

        Assert.Equal(
            new[] { "default", "default_invalid", "none", "personal", "personal_invalid", "personal_unreadable" },
            cases.Select(c => c.Expect.Outcome).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(cases, c => c.Entries.Any(e => e.Payload == "edl-legacy"));
        Assert.Contains(cases, c => c.Entries.Any(e => e.Unreadable));
        Assert.Contains(cases, c => c.Entries.Any(e => e.Directory));
        Assert.Equal(new[] { "Movie - 1080p.mkv", "Movie - 2160p.mkv", "Movie.mkv" }, cases.Select(c => c.Media).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Case_materializes_exactly_its_manifest_entries(string name)
    {
        SelectionCase selectionCase = ExportLayoutFixture.Case(name);
        using var directory = new TempDirectory();

        SelectionCaseMaterializer.Materialize(selectionCase, directory.Path);

        IEnumerable<SelectionEntry> files = selectionCase.Entries.Where(e => !e.Directory);
        Assert.Equal(
            files.Select(e => e.Path).Append(selectionCase.Media).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(directory.Path, p).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal));
        foreach (SelectionEntry entry in selectionCase.Entries)
        {
            string path = SelectionCaseMaterializer.PathOf(directory.Path, entry);
            if (entry.Directory)
            {
                Assert.True(Directory.Exists(path), entry.Path);
            }
            else if (entry.Unreadable)
            {
                Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path));
                Assert.Equal(ExportLayoutFixture.PayloadBytes(entry.Payload!).Length, new FileInfo(path).Length);
            }
            else
            {
                Assert.Equal(ExportLayoutFixture.PayloadBytes(entry.Payload!), File.ReadAllBytes(path));
            }
        }
    }

    [Theory]
    [MemberData(nameof(EdlCases))]
    public async Task Current_plugin_serves_only_the_adjacent_legacy_sidecar(string name)
    {
        SelectionCase selectionCase = ExportLayoutFixture.Case(name);
        using var directory = new TempDirectory();
        string mediaPath = SelectionCaseMaterializer.Materialize(selectionCase, directory.Path);

        IActionResult result = await ServeLegacyAsync(mediaPath);

        string legacyPath = Path.GetFileNameWithoutExtension(selectionCase.Media) + ".edl";
        SelectionEntry? legacy = selectionCase.Entries.SingleOrDefault(e => e.Path == legacyPath);
        if (legacy is null)
        {
            Assert.IsType<NotFoundResult>(result);
        }
        else if (ExportLayoutFixture.Manifest.Payloads[legacy.Payload!].Valid)
        {
            var content = Assert.IsType<ContentResult>(result);
            Assert.Equal(Encoding.UTF8.GetString(ExportLayoutFixture.PayloadBytes(legacy.Payload!)), content.Content);
        }
        else
        {
            Assert.IsType<UnprocessableEntityObjectResult>(result);
        }

        _output.WriteLine($"{name}: legacy selector {StatusOf(result)}, planned contract {selectionCase.Expect.Outcome}");
    }

    private static Task<IActionResult> ServeLegacyAsync(string mediaPath)
    {
        var resolver = new Mock<ILibraryItemResolver>();
        resolver.Setup(x => x.Resolve(It.IsAny<Guid>())).Returns(new ResolvedLibraryItem(true, true, mediaPath));
        // Jellyfin's ManagedFileSystem is not in the reference packages; it answers FileExists with File.Exists.
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(x => x.FileExists(It.IsAny<string>())).Returns<string>(File.Exists);
        var lookup = new EdlLookupService(resolver.Object, fileSystem.Object, NullLogger<EdlLookupService>.Instance);
        var controller = new EdlController(lookup, new EdlParser(), NullLogger<EdlController>.Instance);
        return controller.GetByItemId(Guid.NewGuid(), CancellationToken.None);
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ContentResult => 200,
        NotFoundResult => 404,
        UnprocessableEntityObjectResult => 422,
        _ => 0,
    };
}
