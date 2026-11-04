using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage.Internal;

internal static partial class StorageTempVolumeSpace
{
    internal static long AvailableBytes(string directory)
    {
        string existing = directory;
        while (!Directory.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.Ordinal))
            { throw new IOException(ErrorMessages.TempSpaceProbeFailed); }
            existing = parent;
        }
        if (!OperatingSystem.IsWindows()) { return new DriveInfo(existing).AvailableFreeSpace; }
        // DriveInfo collapses Windows directories to the drive-letter root. Probe the directory
        // itself so mounted volumes and UNC shares enforce the reserve on the actual volume.
        string normalized = existing.TrimEnd('\\', '/') + "\\";
        if (normalized.Length >= 260 && !normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized.StartsWith(@"\\.\", StringComparison.Ordinal) ? @"\\?\" + normalized[4..]
                : normalized.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + normalized[2..] : @"\\?\" + normalized;
        }
        if (!GetDiskFreeSpaceEx(normalized, out ulong available, out _, out _))
        { throw new IOException(ErrorMessages.TempSpaceProbeFailed, new Win32Exception(Marshal.GetLastPInvokeError())); }
        return available > long.MaxValue ? long.MaxValue : (long)available;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
