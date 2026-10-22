using System.Diagnostics;
using System.Runtime.Versioning;
using Jellyfin.Plugin.Edl.Models;
using Jellyfin.Plugin.Edl.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// Selection rules of editarr-export-layout-1 that the frozen v1 manifest has no case for: symbolic links,
/// named pipes, a link swapped in after the check, linked export folders, an unsearchable personal directory,
/// an unreadable default, refused media names, and a server API key. Each case pairs the selector outcome
/// with the HTTP status of the format's route and runs for EDL and for MCF. These cases are local to the plugin
/// and do not change v1.
/// </summary>
[UnsupportedOSPlatform("windows")]
public abstract class SupplementalSelectionTests : IDisposable
{
    private static readonly Guid UserId = ExportLayoutFixture.UserId("userA");

    private readonly TempDirectory _directory = new();
    private readonly string _format;

    protected SupplementalSelectionTests(string format)
    {
        _format = format;
    }

    private string PersonalDirectory => $"{_format}/{UserId:N}";

    private string Personal => $"{PersonalDirectory}/Movie.user-{UserId:N}.{_format}";

    private string Default => $"{_format}/Movie.{_format}";

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
        Write("elsewhere/personal.edl", "personal-a");
        Write(Default, "default");
        Link(Personal, "elsewhere/personal.edl");

        await AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422);
    }

    [Fact]
    public async Task Dangling_symlink_at_the_personal_path_is_invalid()
    {
        Write(Default, "default");
        Link(Personal, "elsewhere/missing.edl");

        await AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422);
    }

    [Fact]
    public async Task Symlinked_directory_at_the_personal_path_is_invalid()
    {
        Directory.CreateDirectory(Path.Combine(_directory.Path, "elsewhere"));
        Write(Default, "default");
        Link(Personal, "elsewhere");

        await AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422);
    }

    [Fact]
    public async Task Linked_export_folders_are_allowed_because_only_the_final_file_is_checked()
    {
        Write($"elsewhere/{_format}/Movie.{_format}", "default");
        Write($"elsewhere/personal/Movie.user-{UserId:N}.{_format}", "personal-a");
        Link(_format, $"elsewhere/{_format}");
        Link(PersonalDirectory, "elsewhere/personal");

        await AssertOutcomeAsync("Movie.mkv", "personal", 200);
        await AssertOutcomeAsync("Movie.mkv", "default", 200, serverKey: true);
    }

    [Fact]
    public async Task Symlink_at_the_default_path_is_invalid_when_the_personal_file_is_absent()
    {
        Write("elsewhere/default.edl", "default");
        Link(Default, "elsewhere/default.edl");

        await AssertOutcomeAsync("Movie.mkv", "default_invalid", 422);
        await AssertOutcomeAsync("Movie.mkv", "default_invalid", 422, serverKey: true);
    }

    [Fact]
    public async Task Named_pipe_at_the_personal_path_is_invalid_without_a_writer_and_does_not_fall_back()
    {
        Write(Default, "default");
        await MakeNamedPipeAsync(Personal);

        await WithoutBlockingAsync(() => AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422));
    }

    [Fact]
    public async Task Named_pipe_at_the_default_path_is_invalid_when_the_personal_file_is_absent()
    {
        await MakeNamedPipeAsync(Default);

        await WithoutBlockingAsync(async () =>
        {
            await AssertOutcomeAsync("Movie.mkv", "default_invalid", 422);
            await AssertOutcomeAsync("Movie.mkv", "default_invalid", 422, serverKey: true);
        });
    }

    [Fact]
    public async Task Final_entry_swapped_for_a_symlink_after_the_check_serves_the_checked_file()
    {
        Write(Personal, "personal-a");
        Write("elsewhere/other-user.edl", "personal-b");
        string personal = Path.Combine(_directory.Path, Personal);
        var selector = new ExportSelector(NullLogger<ExportSelector>.Instance)
        {
            BeforeRead = path =>
            {
                string staging = path + ".swap";
                File.CreateSymbolicLink(staging, Path.Combine(_directory.Path, "elsewhere/other-user.edl"));
                File.Move(staging, path, overwrite: true);
            },
        };
        string mediaPath = Path.Combine(_directory.Path, "Movie.mkv");
        Guid itemId = Guid.NewGuid();

        ExportSelection selection = await selector.SelectAsync(
            new AuthorizedMedia(new Requester.Account(UserId), itemId, itemId, mediaPath), PluginHarness.Format(_format), CancellationToken.None);

        Assert.NotNull(new FileInfo(personal).LinkTarget);
        Assert.Equal("personal", selection.Outcome);
        Assert.Equal(Payload("personal-a"), Assert.IsType<ExportSelection.Found>(selection).Content);
    }

    [Fact]
    public async Task Server_key_gets_the_default_whatever_blocks_the_personal_path()
    {
        Write("elsewhere/personal.edl", "personal-a");
        Write(Default, "default");
        Link(Personal, "elsewhere/personal.edl");
        await AssertOutcomeAsync("Movie.mkv", "personal_invalid", 422);
        await AssertOutcomeAsync("Movie.mkv", "default", 200, serverKey: true);

        File.Delete(Path.Combine(_directory.Path, Personal));
        File.SetUnixFileMode(Path.Combine(_directory.Path, PersonalDirectory), UnixFileMode.None);
        await AssertOutcomeAsync("Movie.mkv", "personal_unreadable", 500);
        await AssertOutcomeAsync("Movie.mkv", "default", 200, serverKey: true);
    }

    [Fact]
    public async Task Personal_path_that_cannot_be_inspected_is_unreadable_and_does_not_fall_back()
    {
        Write(Personal, "personal-a");
        Write(Default, "default");
        File.SetUnixFileMode(Path.Combine(_directory.Path, PersonalDirectory), UnixFileMode.None);

        await AssertOutcomeAsync("Movie.mkv", "personal_unreadable", 500);
    }

    [Fact]
    public async Task Unreadable_default_is_reported_when_the_personal_file_is_absent()
    {
        Write(Default, "default");
        File.SetUnixFileMode(Path.Combine(_directory.Path, Default), UnixFileMode.None);

        await AssertOutcomeAsync("Movie.mkv", "default_unreadable", 500);
        await AssertOutcomeAsync("Movie.mkv", "default_unreadable", 500, serverKey: true);
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData("Movie.")]
    public async Task Refused_media_name_has_no_export_even_where_a_naive_stem_would_find_one(string media)
    {
        Write(Personal, "personal-a");
        Write(Default, "default");
        Write($"{PersonalDirectory}/.user-{UserId:N}.{_format}", "personal-a");
        Write($"{_format}/.{_format}", "default");

        await AssertOutcomeAsync(media, "none", 404);
    }

    private async Task AssertOutcomeAsync(string media, string outcome, int status, bool serverKey = false)
    {
        string mediaPath = Path.Combine(_directory.Path, media);
        File.WriteAllBytes(mediaPath, Array.Empty<byte>());
        Guid itemId = Guid.NewGuid();
        Requester requester = serverKey ? Requester.ServerKey.Instance : new Requester.Account(UserId);

        ExportSelection selection = await new ExportSelector(NullLogger<ExportSelector>.Instance).SelectAsync(
            new AuthorizedMedia(requester, itemId, itemId, mediaPath), PluginHarness.Format(_format), CancellationToken.None);
        var plugin = new PluginHarness();
        var user = PluginHarness.NewUser(UserId);
        Guid item = plugin.AddVideo(new[] { user }, PluginHarness.Source(itemId, mediaPath));
        PluginResponse response = serverKey ? await plugin.GetAsync(null, item, apiKey: true, format: _format) : await plugin.GetAsync(user, item, format: _format);

        Assert.Equal(outcome, selection.Outcome);
        Assert.Equal(status, response.Status);
    }

    private static async Task WithoutBlockingAsync(Func<Task> assertion)
    {
        Task pending = Task.Run(assertion);
        if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))) != pending)
        {
            Assert.Fail("Selection blocked on a named pipe waiting for a writer.");
        }

        await pending;
    }

    private async Task MakeNamedPipeAsync(string relativePath)
    {
        string path = Path.Combine(_directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Process process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path } })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private byte[] Payload(string name) => ExportLayoutFixture.PayloadBytes($"{_format}-{name}");

    private void Write(string relativePath, string payload)
    {
        string path = Path.Combine(_directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Payload(payload));
    }

    private void Link(string relativePath, string relativeTarget)
    {
        string path = Path.Combine(_directory.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, Path.Combine(_directory.Path, relativeTarget));
    }
}

[UnsupportedOSPlatform("windows")]
public sealed class EdlSupplementalSelectionTests() : SupplementalSelectionTests("edl")
{
}

[UnsupportedOSPlatform("windows")]
public sealed class McfSupplementalSelectionTests() : SupplementalSelectionTests("mcf")
{
}
