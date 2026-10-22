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
/// and macOS one <c>open</c> refuses a final symbolic link and cannot block on a named pipe, and the type
/// check uses the opened descriptor, so the caller reads exactly the entry that was checked. Windows and
/// other platforms inspect the entry and then reopen it by path, so a link swapped in between is followed.
/// </summary>
internal static class ExportFile
{
    private const int NoSuchEntry = 2;
    private const int NotADirectory = 20;
    private const int FileTypeMask = 0xF000;
    private const int RegularFileType = 0x8000;

    private static readonly UnixAbi? Abi = RuntimeInformation.ProcessArchitecture switch
    {
        // Flags are O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC. ModeOffset is st_mode in the plain fstat symbol's struct.
        Architecture.X64 when OperatingSystem.IsLinux() => new(0x800 | 0x20000 | 0x80000, 40, 24),
        Architecture.Arm64 when OperatingSystem.IsLinux() => new(0x800 | 0x8000 | 0x80000, 40, 16),
        Architecture.X64 when OperatingSystem.IsMacOS() => new(0x4 | 0x100 | 0x1000000, 62, 8),
        Architecture.Arm64 when OperatingSystem.IsMacOS() => new(0x4 | 0x100 | 0x1000000, 62, 4),
        _ => null,
    };

    /// <summary>Opens <paramref name="path"/> when it is a regular file. The caller owns the returned handle.</summary>
    /// <param name="path">The export path.</param>
    /// <returns>The entry kind, with an open handle only for <see cref="ExportEntry.Regular"/>.</returns>
    /// <exception cref="IOException">The entry exists but cannot be inspected or opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The entry cannot be inspected or opened.</exception>
    public static (ExportEntry Entry, SafeFileHandle? Handle) Open(string path) =>
        Abi is { } abi ? OpenWithoutFollowing(path, abi) : OpenAfterInspecting(path);

    private static (ExportEntry, SafeFileHandle?) OpenWithoutFollowing(string path, UnixAbi abi)
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
        byte[] status = new byte[256];
        if (NativeFstat(fd, status) != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(Marshal.GetPInvokeErrorMessage(errno), errno);
        }

        if ((BitConverter.ToUInt16(status, abi.ModeOffset) & FileTypeMask) != RegularFileType)
        {
            handle.Dispose();
            return (ExportEntry.NotRegular, null);
        }

        return (ExportEntry.Regular, handle);
    }

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

    private sealed record UnixAbi(int OpenFlags, int LinkLoopErrno, int ModeOffset);
}
