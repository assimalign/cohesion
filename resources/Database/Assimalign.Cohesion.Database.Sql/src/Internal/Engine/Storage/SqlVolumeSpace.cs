using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal static partial class SqlVolumeSpace
{
    internal static long GetAvailableBytes(string directory)
    {
        if (!OperatingSystem.IsWindows()) { return new DriveInfo(directory).AvailableFreeSpace; }
        // Query the directory itself: DriveInfo normalizes Windows paths to a drive
        // root, which can name another volume for mount points or UNC shares.
        if (!GetDiskFreeSpaceEx(NormalizeDirectory(directory), out ulong available, out _, out _))
        { throw new IOException(ErrorMessages.JointSpaceProbeFailed, new Win32Exception(Marshal.GetLastPInvokeError())); }
        return available > long.MaxValue ? long.MaxValue : (long)available;
    }

    internal static string NormalizeDirectory(string directory)
    {
        // GetDiskFreeSpaceEx requires a trailing backslash for UNC directories.
        string normalized = directory.TrimEnd('\\', '/') + "\\";
        if (normalized.Length < 260 || normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) { return normalized; }
        if (normalized.StartsWith(@"\\.\", StringComparison.Ordinal)) { return @"\\?\" + normalized[4..]; }
        return normalized.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + normalized[2..] : @"\\?\" + normalized;
    }

    // Resolve the actual mount point rather than the drive-letter root. Unknown
    // volumes conservatively retain the sparse reserve; this never mutates a file.
    internal static unsafe DriveType GetDriveType(string dataPath)
    {
        const int capacity = 32768;
        char* volume = stackalloc char[capacity];
        if (!GetVolumePathName(dataPath, volume, capacity)) { return DriveType.Unknown; }
        return (DriveType)GetVolumeDriveType(new string(volume));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathName(string path, char* volume, int capacity);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetVolumeDriveType(string volume);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
