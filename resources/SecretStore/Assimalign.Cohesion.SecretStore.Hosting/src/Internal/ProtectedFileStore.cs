using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

internal static class ProtectedFileStore
{
    internal static void HardenKeyDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return;
        }

        SetPrivateDirectoryMode(path);
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            SetPrivateFileMode(file);
        }
    }

    internal static async Task<byte[]?> ReadAsync(
        string path,
        IDataProtector protector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        if (!File.Exists(path))
        {
            return null;
        }

        byte[] protectedData = await File.ReadAllBytesAsync(path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return protector.Unprotect(protectedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedData);
        }
    }

    internal static async Task WriteAsync(
        string path,
        IDataProtector protector,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A protected file path must have a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        SetPrivateDirectoryMode(directory);
        byte[] protectedData = protector.Protect(plaintext.Span);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, fileOptions))
            {
                await stream.WriteAsync(protectedData, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
            SetPrivateFileMode(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedData);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
