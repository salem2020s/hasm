using System.Runtime.InteropServices;

namespace Hasm.Infrastructure.Backup;

/// <summary>The identity of a file or folder on its volume: the volume (device or volume serial), the index (inode or file index) and the hard-link count.</summary>
public readonly record struct FileIdentityInfo(ulong Volume, ulong Index, long Links);

/// <summary>
/// Reads the identity of a file or folder (Linux x86-64 and arm64 through <c>stat</c>; Windows is implemented but DISABLED, see <see cref="WindowsVerified"/>). Returns null when it cannot be read (another platform or
/// architecture, glibc before 2.33 without a <c>stat</c> symbol, a path that cannot be opened): a null is never evidence of independence, and callers that need it report <c>link_check_unavailable</c>
/// instead of claiming nothing (review U2 of PR 214). The Windows branch is not exercised by the test suite (no Windows host), so it is disabled and returns null until it has been verified on one.
/// </summary>
public static class FileIdentity
{
    /// <summary>False until the Windows branch (<c>GetFileInformationByHandle</c>) has been verified on a real Windows host; pinned by a test. Changing it is the follow-up that records that verification.</summary>
    internal const bool WindowsVerified = false;

    public static FileIdentityInfo? TryGet(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return Linux(path);
            // the Windows branch is DISABLED until it has been verified on a real Windows host (none is available to the test suite): it returns null, so the commands report link_check_unavailable (exit 8)
            if (OperatingSystem.IsWindows()) return WindowsVerified ? Windows(path) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static FileIdentityInfo? Linux(string path)
    {
        var nlinkOffset = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 16,     // struct stat: st_dev 0 (8), st_ino 8 (8), st_nlink 16 (8)
            Architecture.Arm64 => 20,   // struct stat: st_dev 0 (8), st_ino 8 (8), st_mode 16 (4), st_nlink 20 (4)
            _ => -1,
        };
        if (nlinkOffset < 0) return null;
        var buffer = new byte[512];
        if (LinuxNative.stat(path, buffer) != 0) return null;
        var links = nlinkOffset == 16 ? BitConverter.ToInt64(buffer, 16) : BitConverter.ToUInt32(buffer, 20);
        return new(BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8), links);
    }

    private static FileIdentityInfo? Windows(string path)
    {
        const uint FileFlagBackupSemantics = 0x02000000, ShareAll = 7, OpenExisting = 3;
        using var handle = WindowsNative.CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!WindowsNative.GetFileInformationByHandle(handle, out var info)) return null;
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    private static class LinuxNative
    {
        [DllImport("libc", SetLastError = true)] internal static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buf);
    }

    /// <summary>
    /// The native <c>BY_HANDLE_FILE_INFORMATION</c> (52 bytes): the three FILETIMEs are two 32-bit halves each, because a FILETIME is 4-byte aligned (a <c>long</c> would be 8-byte aligned and
    /// make the managed struct 56 bytes, shifting every later field: review H1 of PR 224). Offsets: FileAttributes 0, the three times 4 to 27, VolumeSerialNumber 28, FileSizeHigh 32, FileSizeLow 36,
    /// NumberOfLinks 40, FileIndexHigh 44, FileIndexLow 48. Pinned by a unit test that runs on Linux.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    private static class WindowsNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);
    }
}

/// <summary>
/// Whether a destination run folder shares storage with the primary's run of the same id (review U1 of PR 214): the folder itself has the same volume and index (a bind mount, a mount above the
/// destination, a link), or any file of it has the same volume and index as the primary's file of the same name (a hard link). Such a "copy" is the primary's own storage and protects against nothing.
/// Reflinked clones have their own inodes and are not detected. <see cref="Unavailable"/> is true when an identity could not be read (nothing is claimed).
/// </summary>
public static class ReplicaIndependence
{
    public sealed record Result(bool Shares, bool Unavailable);

    public static Result Check(string destinationRunDir, string primaryRunDir, Func<string, FileIdentityInfo?>? probe = null)
    {
        probe ??= FileIdentity.TryGet;
        var unavailable = false;
        var a = probe(destinationRunDir); var b = probe(primaryRunDir);
        if (a is null || b is null) return new(false, true);
        if (a.Value.Volume == b.Value.Volume && a.Value.Index == b.Value.Index) return new(true, false);
        foreach (var file in new DirectoryInfo(primaryRunDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(primaryRunDir, file.FullName);
            var theirs = Path.Combine(destinationRunDir, rel);
            if (!File.Exists(theirs)) continue;
            var x = probe(file.FullName); var y = probe(theirs);
            if (x is null || y is null) { unavailable = true; continue; }
            if (x.Value.Volume == y.Value.Volume && x.Value.Index == y.Value.Index) return new(true, false);
        }
        return new(false, unavailable);
    }
}
