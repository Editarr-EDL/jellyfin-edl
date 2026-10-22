using System.Runtime.Versioning;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Materializes the 19 MU-01 selection cases and checks every EDL case against its contract
/// outcome, both from the selector and as the HTTP response. The five MCF cases belong to MU-12.
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
    public async Task Edl_case_yields_its_contract_outcome(string name)
    {
        SelectionCase selectionCase = ExportLayoutFixture.Case(name);
        using var directory = new TempDirectory();
        string mediaPath = SelectionCaseMaterializer.Materialize(selectionCase, directory.Path);
        Guid userId = ExportLayoutFixture.UserId(selectionCase.Principal);
        Guid itemId = Guid.NewGuid();

        ExportSelection selection = await new ExportSelector(NullLogger<ExportSelector>.Instance).SelectAsync(
            new AuthorizedMedia(userId, itemId, itemId, mediaPath), new EdlExportFormat(new EdlParser()), CancellationToken.None);
        var plugin = new PluginHarness();
        var user = PluginHarness.NewUser(userId);
        PluginResponse response = await plugin.GetAsync(user, plugin.AddVideo(new[] { user }, PluginHarness.Source(itemId, mediaPath)));

        Assert.Equal(selectionCase.Expect.Outcome, selection.Outcome);
        Assert.Equal(StatusFor(selectionCase.Expect.Outcome), response.Status);
        if (selectionCase.Expect.Payload is { } payload)
        {
            Assert.Equal(ExportLayoutFixture.PayloadBytes(payload), Assert.IsType<ExportSelection.Found>(selection).Content);
            Assert.Equal(ExportLayoutFixture.PayloadBytes(payload), response.Body);
        }

        _output.WriteLine($"{name}: {selection.Outcome}, HTTP {response.Status}");
    }

    private static int StatusFor(string outcome) => outcome switch
    {
        "personal" or "default" => 200,
        "personal_invalid" or "default_invalid" => 422,
        "personal_unreadable" or "default_unreadable" => 500,
        _ => 404,
    };
}
