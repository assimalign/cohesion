using System;
using System.IO;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Cli.Internal;

internal static class ProtectedFile
{
    internal static byte[] Read(string path)
    {
        byte[] persisted = File.ReadAllBytes(path);
        if (!OperatingSystem.IsWindows())
        {
            return persisted;
        }
        try
        {
            return ProtectedData.Unprotect(persisted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(persisted);
        }
    }

    internal static void Write(string path, byte[] plaintext)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] persisted = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser)
            : plaintext;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                // Set the mode at creation, before any plaintext can be written.
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(persisted);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (!ReferenceEquals(persisted, plaintext))
            {
                CryptographicOperations.ZeroMemory(persisted);
            }
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
