using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Jellyfin.Plugin.Edl.Services;
using Jellyfin.Plugin.Edl.Tests.Contract;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Edl.Tests;

/// <summary>
/// The native open path of <see cref="ExportFile"/>: the descriptor when typing the open file throws, and
/// glibc's pre-2.33 <c>__fxstat</c> binding, which a modern glibc never selects on its own.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ExportFileTests : IDisposable
{
    private static readonly ExportFile.UnixAbi Abi =
        ExportFile.Abi ?? throw new PlatformNotSupportedException("ExportFile has no native row for this platform.");

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Descriptor_is_closed_when_typing_the_open_file_throws()
    {
        string path = Write("Movie.edl", "1 2 3\n");
        SafeFileHandle? opened = null;

        Assert.Throws<EntryPointNotFoundException>(() => ExportFile.OpenWithoutFollowing(path, Abi, (handle, _) =>
        {
            opened = handle;
            throw new EntryPointNotFoundException();
        }));

        Assert.True(opened!.IsClosed);
    }

    [GlibcFact]
    public async Task Legacy_glibc_stat_reads_a_regular_file_and_refuses_a_directory()
    {
        Func<SafeFileHandle, byte[], int> legacy = ExportFile.LegacyStat(Abi.LegacyStatVersion!.Value);
        string regular = Write("Movie.edl", "1 2 3\n");

        var (entry, handle) = ExportFile.OpenWithoutFollowing(regular, Abi, legacy);
        using (handle)
        {
            Assert.Equal(ExportEntry.Regular, entry);
            using var stream = new FileStream(handle!, FileAccess.Read, 0);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("1 2 3\n", await reader.ReadToEndAsync());
        }

        Assert.Equal((ExportEntry.NotRegular, (SafeFileHandle?)null), ExportFile.OpenWithoutFollowing(_directory.Path, Abi, legacy));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory.Path, name);
        File.WriteAllText(path, content);
        return path;
    }
}

/// <summary>A fact that runs only on Linux glibc, where the plugin may bind <c>__fxstat</c>.</summary>
public sealed class GlibcFactAttribute : FactAttribute
{
    public GlibcFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal))
        {
            Skip = "glibc's __fxstat exists only on Linux glibc.";
        }
    }
}
