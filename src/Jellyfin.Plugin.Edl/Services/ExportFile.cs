using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Edl.Services;

/// <summary>What <see cref="ExportFile.Open"/> found at an export path.</summary>
internal enum ExportEntry
{
    Absent,
    NotRegular,
    Regular,
}

/// <summary>
/// Opens an export path for reading only when its final entry is a regular file. On Linux x64 and arm64
/// (glibc or musl) and macOS x64 and arm64, one <c>open</c> refuses a final symbolic link and cannot block
/// on a named pipe, and the type check uses the opened descriptor, so the caller reads exactly the entry
/// that was checked. Every other platform, including Windows, Linux Arm32, ppc64le, and s390x, and FreeBSD,
/// inspects the entry and then reopens it by path, so a link swapped in between is followed.
/// </summary>
internal static class ExportFile
{
    private const int NoSuchEntry = 2;
    private const int NotADirectory = 20;
    private const int FileTypeMask = 0xF000;
    private const int RegularFileType = 0x8000;

    internal static readonly UnixAbi? Abi = RuntimeInformation.ProcessArchitecture switch
    {
        // Flags are O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC. ModeOffset is st_mode in struct stat.
        // LegacyStatVersion is glibc's _STAT_VER for __fxstat, which glibc before 2.33 exports instead of fstat.
        Architecture.X64 when OperatingSystem.IsLinux() => new(0x800 | 0x20000 | 0x80000, 40, 24, 1),
        Architecture.Arm64 when OperatingSystem.IsLinux() => new(0x800 | 0x8000 | 0x80000, 40, 16, 0),
        Architecture.X64 when OperatingSystem.IsMacOS() => new(0x4 | 0x100 | 0x1000000, 62, 8, null),
        Architecture.Arm64 when OperatingSystem.IsMacOS() => new(0x4 | 0x100 | 0x1000000, 62, 4, null),
        _ => null,
    };

    private static readonly Func<SafeFileHandle, byte[], int> Stat =
        Abi is { LegacyStatVersion: int version } && !LibcExports("fstat") ? LegacyStat(version) : CurrentStat;

    /// <summary>Opens <paramref name="path"/> when it is a regular file. The caller owns the returned handle.</summary>
    /// <param name="path">The export path.</param>
    /// <returns>The entry kind, with an open handle only for <see cref="ExportEntry.Regular"/>.</returns>
    /// <exception cref="IOException">The entry exists but cannot be inspected or opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The entry cannot be inspected or opened.</exception>
    public static (ExportEntry Entry, SafeFileHandle? Handle) Open(string path) =>
        Abi is { } abi ? OpenWithoutFollowing(path, abi, Stat) : OpenAfterInspecting(path);

    /// <summary>Opens without following a final link and types the open descriptor with <paramref name="stat"/>.</summary>
    /// <remarks>The descriptor is closed on every path that does not return it, including a throwing <paramref name="stat"/>.</remarks>
    internal static (ExportEntry Entry, SafeFileHandle? Handle) OpenWithoutFollowing(string path, UnixAbi abi, Func<SafeFileHandle, byte[], int> stat)
    {
        int fd = NativeOpen(path, abi.OpenFlags);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            return errno == NoSuchEntry || errno == NotADirectory ? (ExportEntry.Absent, null)
                : errno == abi.LinkLoopErrno ? (ExportEntry.NotRegular, null)
                : throw new IOException(Marshal.GetPInvokeErrorMessage(errno), errno);
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        bool regular = false;
        try
        {
            byte[] status = new byte[256];
            if (stat(handle, status) != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new IOException(Marshal.GetPInvokeErrorMessage(errno), errno);
            }

            regular = (BitConverter.ToUInt16(status, abi.ModeOffset) & FileTypeMask) == RegularFileType;
        }
        finally
        {
            if (!regular)
            {
                handle.Dispose();
            }
        }

        return regular ? (ExportEntry.Regular, handle) : (ExportEntry.NotRegular, null);
    }

    /// <summary>Returns glibc's pre-2.33 <c>__fxstat</c> entry point bound to <paramref name="version"/>.</summary>
    internal static Func<SafeFileHandle, byte[], int> LegacyStat(int version) =>
        (handle, status) => NativeLegacyFstat(version, (int)handle.DangerousGetHandle(), status);

    private static int CurrentStat(SafeFileHandle handle, byte[] status) => NativeFstat((int)handle.DangerousGetHandle(), status);

    private static bool LibcExports(string symbol) =>
        NativeLibrary.TryLoad("libc", typeof(ExportFile).Assembly, null, out IntPtr libc) && NativeLibrary.TryGetExport(libc, symbol, out _);

    private static (ExportEntry, SafeFileHandle?) OpenAfterInspecting(string path)
    {
        // FileInfo reads the entry itself without following a link. A missing path, or a file
        // where a directory should be, reads as -1. Any other failure throws.
        FileAttributes attributes = new FileInfo(path).Attributes;
        if ((int)attributes == -1)
        {
            return (ExportEntry.Absent, null);
        }

        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            ? (ExportEntry.NotRegular, null)
            : (ExportEntry.Regular, File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int NativeFstat(int fd, byte[] status);

    [DllImport("libc", EntryPoint = "__fxstat", SetLastError = true)]
    private static extern int NativeLegacyFstat(int version, int fd, byte[] status);

    internal sealed record UnixAbi(int OpenFlags, int LinkLoopErrno, int ModeOffset, int? LegacyStatVersion);
}
