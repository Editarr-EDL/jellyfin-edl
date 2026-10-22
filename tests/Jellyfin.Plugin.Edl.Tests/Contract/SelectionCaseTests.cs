using System.Runtime.Versioning;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Materializes the 19 MU-01 selection cases and checks each, EDL and MCF, against its contract outcome,
/// both from the selector and as the HTTP response of its format's route, for the case's user and for a
/// server API key. The other format's route must ignore the case's files.
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

    [Fact]
    public void Both_formats_have_cases()
    {
        Assert.Equal(
            new[] { ("edl", 14), ("mcf", 5) },
            ExportLayoutFixture.Manifest.Selection.GroupBy(c => c.Format).Select(g => (g.Key, g.Count())).Order());
    }

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
    [MemberData(nameof(AllCases))]
    public async Task Case_yields_its_contract_outcome(string name)
    {
        SelectionCase selectionCase = ExportLayoutFixture.Case(name);
        using var directory = new TempDirectory();
        string mediaPath = SelectionCaseMaterializer.Materialize(selectionCase, directory.Path);
        Guid userId = ExportLayoutFixture.UserId(selectionCase.Principal);
        Guid itemId = Guid.NewGuid();

        ExportSelection selection = await new ExportSelector(NullLogger<ExportSelector>.Instance).SelectAsync(
            new AuthorizedMedia(new Requester.Account(userId), itemId, itemId, mediaPath), PluginHarness.Format(selectionCase.Format), CancellationToken.None);
        var plugin = new PluginHarness();
        var user = PluginHarness.NewUser(userId);
        Guid item = plugin.AddVideo(new[] { user }, PluginHarness.Source(itemId, mediaPath));
        PluginResponse response = await plugin.GetAsync(user, item, format: selectionCase.Format);

        Assert.Equal(selectionCase.Expect.Outcome, selection.Outcome);
        Assert.Equal(StatusFor(selectionCase.Expect.Outcome), response.Status);
        if (selectionCase.Expect.Payload is { } payload)
        {
            Assert.Equal(ExportLayoutFixture.PayloadBytes(payload), Assert.IsType<ExportSelection.Found>(selection).Content);
            Assert.Equal(ExportLayoutFixture.PayloadBytes(payload), response.Body);
        }

        _output.WriteLine($"{name}: {selection.Outcome}, HTTP {response.Status}");
    }

    /// <summary>
    /// The other format's route never serves the case's own-format files, and finds nothing when the case has
    /// no file of that format. One MCF case also holds a personal EDL, which the EDL route serves.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCases))]
    public async Task Case_files_are_invisible_to_the_other_format(string name)
    {
        SelectionCase selectionCase = ExportLayoutFixture.Case(name);
        using var directory = new TempDirectory();
        string mediaPath = SelectionCaseMaterializer.Materialize(selectionCase, directory.Path);
        var plugin = new PluginHarness();
        var user = PluginHarness.NewUser(ExportLayoutFixture.UserId(selectionCase.Principal));
        Guid item = plugin.AddVideo(new[] { user }, PluginHarness.Source(Guid.NewGuid(), mediaPath));
        string other = selectionCase.Format == "edl" ? "mcf" : "edl";
        ILookup<string, string> payloads = selectionCase.Entries.Where(e => e.Payload is not null)
            .ToLookup(e => ExportLayoutFixture.Manifest.Payloads[e.Payload!].Format, e => e.Payload!);

        foreach (PluginResponse response in new[] { await plugin.GetAsync(user, item, format: other), await plugin.GetAsync(null, item, apiKey: true, format: other) })
        {
            Assert.DoesNotContain(response.Body, payloads[selectionCase.Format].Select(ExportLayoutFixture.PayloadBytes));
            if (!payloads[other].Any())
            {
                Assert.Equal(404, response.Status);
            }
        }
    }

    /// <summary>
    /// A server API key gets what the case's default path alone decides, whatever personal files the case holds,
    /// including when the request also names the case's user.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCases))]
    public async Task Case_yields_only_its_default_for_a_server_key(string name)
    {
        SelectionCase selectionCase = ExportLayoutFixture.Case(name);
        using var directory = new TempDirectory();
        string mediaPath = SelectionCaseMaterializer.Materialize(selectionCase, directory.Path);
        var plugin = new PluginHarness();
        Guid itemId = plugin.AddVideo(Array.Empty<User>(), PluginHarness.Source(Guid.NewGuid(), mediaPath));
        var user = PluginHarness.NewUser(ExportLayoutFixture.UserId(selectionCase.Principal));
        string defaultPath = $"{selectionCase.Format}/{Path.GetFileNameWithoutExtension(selectionCase.Media)}.{selectionCase.Format}";
        SelectionEntry? entry = selectionCase.Entries.SingleOrDefault(e => e.Path == defaultPath);
        (string Outcome, string? Payload) expected = entry switch
        {
            null => ("none", null),
            { Directory: true } => ("default_invalid", null),
            { Unreadable: true } => ("default_unreadable", null),
            { Payload: { } id } when ExportLayoutFixture.Manifest.Payloads[id].Valid => ("default", id),
            _ => ("default_invalid", null),
        };

        ExportSelection selection = await new ExportSelector(NullLogger<ExportSelector>.Instance).SelectAsync(
            new AuthorizedMedia(Requester.ServerKey.Instance, itemId, itemId, mediaPath), PluginHarness.Format(selectionCase.Format), CancellationToken.None);
        PluginResponse key = await plugin.GetAsync(null, itemId, apiKey: true, format: selectionCase.Format);
        PluginResponse keyNamingUser = await plugin.GetAsync(user, itemId, apiKey: true, format: selectionCase.Format);

        Assert.Equal(expected.Outcome, selection.Outcome);
        Assert.All(new[] { key, keyNamingUser }, response =>
        {
            Assert.Equal(StatusFor(expected.Outcome), response.Status);
            if (expected.Payload is { } payload)
            {
                Assert.Equal(ExportLayoutFixture.PayloadBytes(payload), response.Body);
            }
        });
        _output.WriteLine($"{name} as server key: {selection.Outcome}, HTTP {key.Status}");
    }

    private static int StatusFor(string outcome) => outcome switch
    {
        "personal" or "default" => 200,
        "personal_invalid" or "default_invalid" => 422,
        "personal_unreadable" or "default_unreadable" => 500,
        _ => 404,
    };
}
