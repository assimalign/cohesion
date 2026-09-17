using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Provides access to one materialized resource mount.
/// </summary>
/// <remarks>
/// <see cref="Path"/> is the raw carrier path. On Windows, configuration and secret mount
/// files at that path contain CurrentUser DPAPI-protected bytes; use <see cref="OpenRead"/>
/// or <see cref="ReadAllBytes"/> when plaintext is required.
/// </remarks>
public sealed class ResourceMount
{
    private readonly byte[]? _content;

    /// <summary>
    /// Initializes a file-backed resource mount.
    /// </summary>
    /// <param name="path">The raw materialized path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public ResourceMount(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = path;
    }

    private ResourceMount(byte[] content, string? path)
    {
        _content = content;
        Path = path;
    }

    /// <summary>
    /// Gets the raw materialized path, or <see langword="null"/> for an in-memory mount.
    /// </summary>
    /// <remarks>
    /// On Windows, a file at this path is protected ciphertext. The property remains
    /// available for tools that must receive a path rather than plaintext bytes.
    /// </remarks>
    public string? Path { get; }

    /// <summary>
    /// Creates an in-memory resource mount for an in-process resource invocation.
    /// </summary>
    /// <param name="content">The plaintext mount content.</param>
    /// <param name="path">An optional raw path exposed to path-based tools.</param>
    /// <returns>A mount that opens the supplied plaintext content.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static ResourceMount FromBytes(ReadOnlySpan<byte> content, string? path = null)
    {
        if (path is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
        }

        return new ResourceMount(content.ToArray(), path);
    }

    /// <summary>
    /// Opens a readable plaintext stream for the mount.
    /// </summary>
    /// <returns>A readable stream owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The mount has neither content nor a path.</exception>
    /// <exception cref="IOException">The file cannot be read or decrypted.</exception>
    public Stream OpenRead()
    {
        if (_content is not null)
        {
            return new MemoryStream(_content, writable: false);
        }

        if (Path is null)
        {
            throw new InvalidOperationException("The resource mount has neither in-memory content nor a materialized path.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return File.OpenRead(Path);
        }

        return new MemoryStream(ReadProtectedFile(Path), writable: false);
    }

    /// <summary>
    /// Reads all plaintext bytes from the mount.
    /// </summary>
    /// <returns>A new array containing the plaintext mount content.</returns>
    /// <exception cref="InvalidOperationException">The mount has neither content nor a path.</exception>
    /// <exception cref="IOException">The file cannot be read or decrypted.</exception>
    public byte[] ReadAllBytes()
    {
        if (_content is not null)
        {
            return (byte[])_content.Clone();
        }

        if (Path is null)
        {
            throw new InvalidOperationException("The resource mount has neither in-memory content nor a materialized path.");
        }

        return OperatingSystem.IsWindows()
            ? ReadProtectedFile(Path)
            : File.ReadAllBytes(Path);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadProtectedFile(string path)
    {
        byte[] protectedBytes = File.ReadAllBytes(path);
        try
        {
            return ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new IOException($"The Windows resource mount at '{path}' could not be decrypted for the current user.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }
}
