using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Persists management intent with cross-process locking and atomic file replacement.</summary>
/// <remarks>Windows content uses CurrentUser DPAPI; POSIX directories/files are owner-only. The directory must survive gateway restarts.</remarks>
[DebuggerDisplay("FileResourceManagementStore")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class FileResourceManagementStore : IResourceManagementStore
{
    private readonly string _directory;

    private FileResourceManagementStore(string directory)
    {
        _directory = directory;
    }

    /// <summary>Creates a store in a persistent gateway-owned directory.</summary>
    /// <param name="directory">The validated storage path.</param>
    /// <returns>The file-backed management store.</returns>
    /// <exception cref="ArgumentException">The directory is default.</exception>
    public static FileResourceManagementStore Create(FileSystemPath directory)
    {
        string directoryPath = directory.ToString();
        if (string.IsNullOrWhiteSpace(directoryPath)) { throw new ArgumentException(ErrorMessages.ManagementDirectoryRequired, nameof(directory)); }
        return new FileResourceManagementStore(Path.GetFullPath(directoryPath));
    }

    /// <inheritdoc />
    public async ValueTask<ResourceManagementDocument?> ReadAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        string path = GetPath(application, resource);
        EnsureDirectory();
        using LocalFileLease lease = await LocalFileLease.AcquireAsync(path + ".lock", true, ErrorMessages.ManagementStoreContention, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path)) { return null; }
        byte[] stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] content = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser) : stored;
        return new ResourceManagementDocument(Convert.ToHexStringLower(SHA256.HashData(stored)), content);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryWriteAsync(ApplicationName application, ResourceName resource, string? expectedVersion, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        string path = GetPath(application, resource);
        EnsureDirectory();
        using LocalFileLease lease = await LocalFileLease.AcquireAsync(path + ".lock", true, ErrorMessages.ManagementStoreContention, cancellationToken).ConfigureAwait(false);
        string? current = File.Exists(path)
            ? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))) : null;
        if (!string.Equals(current, expectedVersion, StringComparison.Ordinal)) { return false; }
        byte[] plaintext = content.ToArray();
        byte[] stored;
        try { stored = OperatingSystem.IsWindows() ? ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser) : plaintext; }
        finally { if (OperatingSystem.IsWindows()) { CryptographicOperations.ZeroMemory(plaintext); } }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(stored, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }

    private string GetPath(ApplicationName application, ResourceName resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Value);
        string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(application.Value + "\0" + resource.Value)));
        return Path.Combine(_directory, identity + ".state");
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(_directory); }
        else
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(_directory, mode);
            File.SetUnixFileMode(_directory, mode);
        }
    }

    private sealed class DebugView
    {
        public DebugView(FileResourceManagementStore value) { }
    }
}
