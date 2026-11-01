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
        if (!GetDiskFreeSpaceEx(directory, out ulong available, out _, out _))
        { throw new IOException(ErrorMessages.JointSpaceProbeFailed, new Win32Exception(Marshal.GetLastPInvokeError())); }
        return available > long.MaxValue ? long.MaxValue : (long)available;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
