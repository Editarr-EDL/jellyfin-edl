using System.Runtime.Versioning;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Selection rules of editarr-export-layout-1 that the frozen v1 manifest has no case for: symbolic links,
/// an unsearchable personal directory, an unreadable default, and refused media names. Each case pairs
/// the selector outcome with the HTTP status. These cases are local to the plugin and do not change v1.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class SupplementalSelectionTests : IDisposable
{
    private static readonly Guid UserId = ExportLayoutFixture.UserId("userA");
    private static readonly string PersonalDirectory = $"edl/{UserId:N}";
    private static readonly string Personal = $"{PersonalDirectory}/Movie.user-{UserId:N}.edl";
    private const string Default = "edl/Movie.edl";

    private readonly TempDirectory _directory = new();

    public void Dispose()
    {
        string locked = Path.Combine(_directory.Path, PersonalDirectory);
        if (Directory.Exists(locked))
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _directory.Dispose();
    }

    [Fact]
    public async Task Symlink_to_a_valid_file_at_the_personal_path_is_invalid_and_does_not_fall_back()
    {
        Write("elsewhere/personal.edl", "edl-personal-a");
        Write(Default, "edl-default");
        Link(Personal, "elsewhere/personal.edl");

        await AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422);
    }

    [Fact]
    public async Task Dangling_symlink_at_the_personal_path_is_invalid()
    {
        Write(Default, "edl-default");
        Link(Personal, "elsewhere/missing.edl");

        await AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422);
    }

    [Fact]
    public async Task Symlinked_directory_at_the_personal_path_is_invalid()
    {
        Directory.CreateDirectory(Path.Combine(_directory.Path, "elsewhere"));
        Write(Default, "edl-default");
        Link(Personal, "elsewhere");

        await AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422);
    }

    [Fact]
    public async Task Symlink_at_the_default_path_is_invalid_when_the_personal_file_is_absent()
    {
        Write("elsewhere/default.edl", "edl-default");
        Link(Default, "elsewhere/default.edl");

        await AssertOutcomeAsync("Movie.mkv", "default_invalid", 422);
    }

    [Fact]
    public async Task Personal_path_that_cannot_be_inspected_is_unreadable_and_does_not_fall_back()
    {
        Write(Personal, "edl-personal-a");
        Write(Default, "edl-default");
        File.SetUnixFileMode(Path.Combine(_directory.Path, PersonalDirectory), UnixFileMode.None);

        await AssertOutcomeAsync("Movie.mkv", "personal_unreadable", 500);
    }

    [Fact]
    public async Task Unreadable_default_is_reported_when_the_personal_file_is_absent()
    {
        Write(Default, "edl-default");
        File.SetUnixFileMode(Path.Combine(_directory.Path, Default), UnixFileMode.None);

        await AssertOutcomeAsync("Movie.mkv", "default_unreadable", 500);
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData("Movie.")]
    public async Task Refused_media_name_has_no_export_even_where_a_naive_stem_would_find_one(string media)
    {
        Write(Personal, "edl-personal-a");
        Write(Default, "edl-default");
        Write($"{PersonalDirectory}/.user-{UserId:N}.edl", "edl-personal-a");
        Write("edl/.edl", "edl-default");

        await AssertOutcomeAsync(media, "none", 404);
    }

    private async Task AssertOutcomeAsync(string media, string outcome, int status)
    {
        string mediaPath = Path.Combine(_directory.Path, media);
        File.WriteAllBytes(mediaPath, Array.Empty<byte>());
        Guid itemId = Guid.NewGuid();

        ExportSelection selection = await new ExportSelector(NullLogger<ExportSelector>.Instance).SelectAsync(
            new AuthorizedMedia(UserId, itemId, itemId, mediaPath), new EdlExportFormat(new EdlParser()), CancellationToken.None);
        var plugin = new PluginHarness();
        var user = PluginHarness.NewUser(UserId);
        PluginResponse response = await plugin.GetAsync(user, plugin.AddVideo(new[] { user }, PluginHarness.Source(itemId, mediaPath)));

        Assert.Equal(outcome, selection.Outcome);
        Assert.Equal(status, response.Status);
    }

    private void Write(string relativePath, string payloadId)
    {
        string path = Path.Combine(_directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ExportLayoutFixture.PayloadBytes(payloadId));
    }

    private void Link(string relativePath, string relativeTarget)
    {
        string path = Path.Combine(_directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, Path.Combine(_directory.Path, relativeTarget));
    }
}
