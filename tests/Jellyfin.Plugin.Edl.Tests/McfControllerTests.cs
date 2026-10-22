using System.Runtime.Versioning;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Edl.Api;
using Jellyfin.Plugin.Edl.Tests.Contract;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Edl.Tests;

/// <summary>
/// Authorization, version, format independence, and cache-validator behavior of GET /Plugins/Edl/Mcf/ByItem/{itemId}.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class McfControllerTests : IDisposable
{
    private static readonly byte[] DefaultMcf = ExportLayoutFixture.PayloadBytes("mcf-default");
    private static readonly byte[] McfA = ExportLayoutFixture.PayloadBytes("mcf-personal-a");
    private static readonly byte[] McfB = ExportLayoutFixture.PayloadBytes("mcf-personal-b");
    private static readonly byte[] EdlA = Encoding.UTF8.GetBytes("10 20 3\n");

    private readonly TempDirectory _directory = new();
    private readonly PluginHarness _plugin = new();
    private readonly User _userA = PluginHarness.NewUser(Guid.Parse("5a4de0c29b1e4f3a8c6d0e1f2a3b4c5d"));
    private readonly User _userB = PluginHarness.NewUser(Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0"));
    private readonly User _userC = PluginHarness.NewUser(Guid.Parse("c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3"));

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Route_is_explicit_and_binds_no_user_parameter()
    {
        var method = typeof(EdlController).GetMethod(nameof(EdlController.GetMcfByItemId))!;

        Assert.Equal("Mcf/ByItem/{itemId:guid}", Assert.Single(method.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>()).Template);
        Assert.Equal(new[] { "itemId", "mediaSourceId", "cancellationToken" }, method.GetParameters().Select(p => p.Name));
    }

    [Fact]
    public async Task Each_user_receives_their_own_mcf_and_a_user_without_one_receives_the_default()
    {
        Guid item = AddMovie(new[] { _userA, _userB, _userC });
        Write(Personal(_userA), McfA);
        Write(Personal(_userB), McfB);
        Write("mcf/Movie.mcf", DefaultMcf);

        AssertServed(McfA, await GetMcf(_userA, item));
        AssertServed(McfB, await GetMcf(_userB, item));
        AssertServed(DefaultMcf, await GetMcf(_userC, item));
    }

    [Fact]
    public async Task Mcf_is_selected_independently_of_edl()
    {
        Guid item = AddMovie(new[] { _userA, _userB });
        Write(Personal(_userA, format: "edl"), EdlA);
        Write("mcf/Movie.mcf", DefaultMcf);
        Write(Personal(_userB), McfB);

        AssertServed(DefaultMcf, await GetMcf(_userA, item));
        Assert.Equal(EdlA, (await _plugin.GetAsync(_userA, item)).Body);
        AssertServed(McfB, await GetMcf(_userB, item));
        Assert.Equal(404, (await _plugin.GetAsync(_userB, item)).Status);
    }

    [Fact]
    public async Task Legacy_adjacent_mcf_is_never_served()
    {
        Guid item = AddMovie(new[] { _userA });
        Write("Movie.mcf", DefaultMcf);

        Assert.Equal(404, (await GetMcf(_userA, item)).Status);
        Assert.Equal(404, (await GetMcf(null, item, apiKey: true)).Status);
    }

    [Fact]
    public async Task Unreadable_personal_mcf_is_500_and_never_replaced_by_the_default()
    {
        Guid item = AddMovie(new[] { _userA });
        Write(Personal(_userA), McfA);
        Write("mcf/Movie.mcf", DefaultMcf);
        File.SetUnixFileMode(Path.Combine(_directory.Path, Personal(_userA)), UnixFileMode.None);

        PluginResponse response = await GetMcf(_userA, item);

        Assert.Equal(500, response.Status);
        Assert.Empty(response.Body);
        Assert.Equal("private, no-cache", response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Invalid_personal_mcf_is_422_while_the_api_key_still_reads_the_default()
    {
        Guid item = AddMovie(new[] { _userA });
        Write(Personal(_userA), ExportLayoutFixture.PayloadBytes("mcf-malformed"));
        Write("mcf/Movie.mcf", DefaultMcf);

        PluginResponse user = await GetMcf(_userA, item);

        Assert.Equal(422, user.Status);
        Assert.Contains("MCF version is not supported", Encoding.UTF8.GetString(user.Body), StringComparison.Ordinal);
        AssertServed(DefaultMcf, await GetMcf(null, item, apiKey: true));
    }

    [Fact]
    public async Task Item_the_user_cannot_see_is_not_found_even_with_a_personal_mcf()
    {
        Guid item = AddMovie(new[] { _userA });
        Write(Personal(_userB), McfB);
        Write("mcf/Movie.mcf", DefaultMcf);

        Assert.Equal(404, (await GetMcf(_userB, item)).Status);
        AssertServed(DefaultMcf, await GetMcf(_userA, item));
    }

    [Fact]
    public async Task Api_key_reads_the_default_mcf_and_never_a_personal_one_even_when_it_names_a_user()
    {
        Guid item = AddMovie(new[] { _userA });
        Write(Personal(_userA), McfA);

        Assert.Equal(404, (await GetMcf(null, item, apiKey: true)).Status);
        Assert.Equal(404, (await GetMcf(_userA, item, apiKey: true)).Status);

        Write("mcf/Movie.mcf", DefaultMcf);
        AssertServed(DefaultMcf, await GetMcf(null, item, apiKey: true));
        AssertServed(DefaultMcf, await GetMcf(_userA, item, apiKey: true));
    }

    [Fact]
    public async Task Request_without_a_user_or_key_gets_403_before_any_lookup()
    {
        Guid item = AddMovie(new[] { _userA });
        Write("mcf/Movie.mcf", DefaultMcf);
        int lookupsBefore = _plugin.LibraryLookups;

        PluginResponse response = await GetMcf(null, item);

        Assert.Equal(403, response.Status);
        Assert.Equal(lookupsBefore, _plugin.LibraryLookups);
    }

    [Fact]
    public async Task Version_selects_its_own_personal_or_default_mcf()
    {
        Guid hd = Guid.NewGuid();
        Guid uhd = Guid.NewGuid();
        _plugin.AddVideo(new[] { _userA, _userB }, Source(hd, "Movie - 1080p.mkv"), Source(uhd, "Movie - 2160p.mkv"));
        Write(Personal(_userA, "Movie - 1080p"), McfA);
        Write("mcf/Movie - 2160p.mcf", DefaultMcf);

        AssertServed(McfA, await GetMcf(_userA, hd, hd));
        AssertServed(DefaultMcf, await GetMcf(_userA, hd, uhd));
        Assert.Equal(404, (await GetMcf(_userB, hd, hd)).Status);
        AssertServed(DefaultMcf, await GetMcf(_userB, hd, uhd));
        Assert.Equal(404, (await GetMcf(_userA, hd, Guid.NewGuid())).Status);
    }

    [Fact]
    public async Task Validators_match_only_the_same_requester_version_and_format()
    {
        Guid item = AddMovie(new[] { _userA, _userB });
        Write(Personal(_userA, format: "edl"), EdlA);
        Write(Personal(_userA), McfA);
        Write("mcf/Movie.mcf", DefaultMcf);

        PluginResponse mcf = await GetMcf(_userA, item);
        PluginResponse edl = await _plugin.GetAsync(_userA, item);
        Assert.NotEqual(mcf.ETag, edl.ETag);

        Assert.Equal(304, (await GetMcf(_userA, item, ifNoneMatch: mcf.ETag)).Status);
        AssertServed(McfA, await GetMcf(_userA, item, ifNoneMatch: edl.ETag));
        Assert.Equal(EdlA, (await _plugin.GetAsync(_userA, item, ifNoneMatch: mcf.ETag)).Body);
        AssertServed(DefaultMcf, await GetMcf(_userB, item, ifNoneMatch: mcf.ETag));
        AssertServed(DefaultMcf, await GetMcf(null, item, ifNoneMatch: mcf.ETag, apiKey: true));
    }

    [Fact]
    public async Task Every_mcf_response_is_private_and_must_be_revalidated()
    {
        Guid item = AddMovie(new[] { _userA, _userB });
        Write(Personal(_userA), McfA);
        Write(Personal(_userB), ExportLayoutFixture.PayloadBytes("mcf-malformed"));

        Assert.All(
            new[] { await GetMcf(_userA, item), await GetMcf(_userB, item), await GetMcf(null, item, apiKey: true) },
            response => Assert.Equal("private, no-cache", response.Headers.CacheControl.ToString()));
    }

    private static string Personal(User user, string stem = "Movie", string format = "mcf") => $"{format}/{user.Id:N}/{stem}.user-{user.Id:N}.{format}";

    private static void AssertServed(byte[] expected, PluginResponse response)
    {
        Assert.Equal(200, response.Status);
        Assert.Equal("text/vtt; charset=utf-8", response.Headers.ContentType.ToString());
        Assert.Equal(expected, response.Body);
    }

    private Task<PluginResponse> GetMcf(User? user, Guid itemId, Guid? mediaSourceId = null, string? ifNoneMatch = null, bool apiKey = false) =>
        _plugin.GetAsync(user, itemId, mediaSourceId, ifNoneMatch, apiKey, format: "mcf");

    private Guid AddMovie(IEnumerable<User> viewers) => _plugin.AddVideo(viewers, Source(Guid.NewGuid(), "Movie.mkv"));

    private MediaBrowser.Model.Dto.MediaSourceInfo Source(Guid id, string name) =>
        PluginHarness.Source(id, Path.Combine(_directory.Path, name));

    private void Write(string relativePath, byte[] content)
    {
        string path = Path.Combine(_directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
