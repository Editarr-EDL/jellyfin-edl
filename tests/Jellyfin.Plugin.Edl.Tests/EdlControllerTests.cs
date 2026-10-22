using System.Runtime.Versioning;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Edl.Api;
using Jellyfin.Plugin.Edl.Tests.Contract;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;

namespace Jellyfin.Plugin.Edl.Tests;

/// <summary>
/// Authorization, version, byte-exactness, and cache-validator behavior of GET /Plugins/Edl/ByItem/{itemId}.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class EdlControllerTests : IDisposable
{
    private static readonly byte[] DefaultEdl = Encoding.UTF8.GetBytes("0 1 3\n");
    private static readonly byte[] EdlA = Encoding.UTF8.GetBytes("10 20 3\n");
    private static readonly byte[] EdlB = Encoding.UTF8.GetBytes("30 40 1\n");

    private readonly TempDirectory _directory = new();
    private readonly PluginHarness _plugin = new();
    private readonly User _userA = PluginHarness.NewUser(Guid.Parse("5a4de0c29b1e4f3a8c6d0e1f2a3b4c5d"));
    private readonly User _userB = PluginHarness.NewUser(Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0"));
    private readonly User _userC = PluginHarness.NewUser(Guid.Parse("c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3"));

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Controller_requires_a_jellyfin_session_or_key()
    {
        Assert.NotEmpty(typeof(EdlController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
    }

    [Fact]
    public async Task Each_user_receives_their_own_file_and_a_user_without_one_receives_the_default()
    {
        Guid item = AddMovie(new[] { _userA, _userB, _userC });
        Write(Personal(_userA), EdlA);
        Write(Personal(_userB), EdlB);
        Write("edl/Movie.edl", DefaultEdl);

        AssertServed(EdlA, await _plugin.GetAsync(_userA, item));
        AssertServed(EdlB, await _plugin.GetAsync(_userB, item));
        AssertServed(DefaultEdl, await _plugin.GetAsync(_userC, item));
    }

    [Fact]
    public async Task Files_are_served_byte_for_byte()
    {
        Guid item = AddMovie(new[] { _userA, _userB });
        byte[] personal = Encoding.UTF8.GetBytes("\uFEFF# note\r\n0 1 3\r\n\r\n");
        byte[] trailingBlankLine = Encoding.UTF8.GetBytes("1 2 3\n\n");
        byte[] loneCarriageReturn = Encoding.UTF8.GetBytes("0 1 3\r1 2 1");
        Write(Personal(_userA), personal);
        Write("edl/Movie.edl", trailingBlankLine);

        AssertServed(personal, await _plugin.GetAsync(_userA, item));
        AssertServed(trailingBlankLine, await _plugin.GetAsync(_userB, item));

        Write("edl/Movie.edl", loneCarriageReturn);
        AssertServed(loneCarriageReturn, await _plugin.GetAsync(_userB, item));
    }

    [Fact]
    public async Task Api_key_gets_403_before_any_lookup_even_when_it_carries_a_user()
    {
        Guid item = AddMovie(new[] { _userA });
        Write(Personal(_userA), EdlA);
        Write("edl/Movie.edl", DefaultEdl);
        int lookupsBefore = _plugin.LibraryLookups;

        PluginResponse keyOnly = await _plugin.GetAsync(null, item, apiKey: true);
        PluginResponse impersonation = await _plugin.GetAsync(_userA, item, apiKey: true);
        PluginResponse noUser = await _plugin.GetAsync(null, item);

        Assert.All(new[] { keyOnly, impersonation, noUser }, response =>
        {
            Assert.Equal(403, response.Status);
            Assert.Empty(response.Body);
        });
        Assert.Equal(lookupsBefore, _plugin.LibraryLookups);
    }

    [Fact]
    public async Task Item_the_user_cannot_see_is_not_found_even_with_a_personal_file()
    {
        Guid item = AddMovie(new[] { _userA });
        Write(Personal(_userB), EdlB);
        Write("edl/Movie.edl", DefaultEdl);

        Assert.Equal(404, (await _plugin.GetAsync(_userB, item)).Status);
        AssertServed(DefaultEdl, await _plugin.GetAsync(_userA, item));
    }

    [Fact]
    public async Task Version_selects_its_own_personal_or_default_file()
    {
        Guid hd = Guid.NewGuid();
        Guid uhd = Guid.NewGuid();
        _plugin.AddVideo(new[] { _userA, _userB }, Source(hd, "Movie - 1080p.mkv"), Source(uhd, "Movie - 2160p.mkv"));
        Write(Personal(_userA, "Movie - 1080p"), EdlA);
        Write("edl/Movie - 2160p.edl", DefaultEdl);

        AssertServed(EdlA, await _plugin.GetAsync(_userA, hd));
        AssertServed(EdlA, await _plugin.GetAsync(_userA, hd, hd));
        AssertServed(DefaultEdl, await _plugin.GetAsync(_userA, hd, uhd));
        Assert.Equal(404, (await _plugin.GetAsync(_userB, hd, hd)).Status);
        AssertServed(DefaultEdl, await _plugin.GetAsync(_userB, hd, uhd));
    }

    [Fact]
    public async Task Version_outside_the_item_or_without_a_local_file_is_not_found()
    {
        Guid item = Guid.NewGuid();
        Guid remote = Guid.NewGuid();
        _plugin.AddVideo(new[] { _userA }, Source(item, "Movie.mkv"), PluginHarness.Source(remote, Path.Combine(_directory.Path, "Remote.mkv"), MediaProtocol.Http));
        Write(Personal(_userA), EdlA);
        Write(Personal(_userA, "Remote"), EdlB);

        Assert.Equal(404, (await _plugin.GetAsync(_userA, item, Guid.NewGuid())).Status);
        Assert.Equal(404, (await _plugin.GetAsync(_userA, item, remote)).Status);
        AssertServed(EdlA, await _plugin.GetAsync(_userA, item, item));
    }

    [Fact]
    public async Task Every_response_is_private_and_must_be_revalidated()
    {
        Guid item = AddMovie(new[] { _userA, _userB });
        Write(Personal(_userA), EdlA);

        Assert.Equal("private, no-cache", (await _plugin.GetAsync(_userA, item)).Headers.CacheControl.ToString());
        Assert.Equal("private, no-cache", (await _plugin.GetAsync(_userB, item)).Headers.CacheControl.ToString());
        Assert.Equal("private, no-cache", (await _plugin.GetAsync(null, item, apiKey: true)).Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Validator_matches_only_the_same_user_version_and_bytes()
    {
        Guid hd = Guid.NewGuid();
        Guid uhd = Guid.NewGuid();
        _plugin.AddVideo(new[] { _userA, _userB }, Source(hd, "Movie - 1080p.mkv"), Source(uhd, "Movie - 2160p.mkv"));
        Write("edl/Movie - 1080p.edl", DefaultEdl);
        Write("edl/Movie - 2160p.edl", DefaultEdl);

        string tagA = (await _plugin.GetAsync(_userA, hd)).ETag;
        Assert.NotEmpty(tagA);

        PluginResponse sameRequest = await _plugin.GetAsync(_userA, hd, ifNoneMatch: tagA);
        Assert.Equal(304, sameRequest.Status);
        Assert.Empty(sameRequest.Body);

        PluginResponse otherUser = await _plugin.GetAsync(_userB, hd, ifNoneMatch: tagA);
        AssertServed(DefaultEdl, otherUser);
        Assert.NotEqual(tagA, otherUser.ETag);

        PluginResponse otherVersion = await _plugin.GetAsync(_userA, hd, uhd, ifNoneMatch: tagA);
        AssertServed(DefaultEdl, otherVersion);
        Assert.NotEqual(tagA, otherVersion.ETag);

        Write(Personal(_userA, "Movie - 1080p"), EdlA);
        AssertServed(EdlA, await _plugin.GetAsync(_userA, hd, ifNoneMatch: tagA));
    }

    private static string Personal(User user, string stem = "Movie") => $"edl/{user.Id:N}/{stem}.user-{user.Id:N}.edl";

    private static void AssertServed(byte[] expected, PluginResponse response)
    {
        Assert.Equal(200, response.Status);
        Assert.Equal("text/plain; charset=utf-8", response.Headers.ContentType.ToString());
        Assert.Equal(expected, response.Body);
    }

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
