using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Path helpers for the static-files middleware and the <c>SendFileAsync</c> response helper:
/// segment-aligned prefix matching, the traversal gate, and the mount lookup that together keep
/// every lookup inside the mounted file-system root.
/// </summary>
internal static class StaticFilePath
{
    /// <summary>
    /// Matches <paramref name="path"/> against a normalized request-path prefix (empty for the
    /// root mount, otherwise <c>/seg[/seg...]</c> with no trailing slash) and returns the
    /// remainder. Matching is segment-aligned — <c>/staticfiles</c> does not match the prefix
    /// <c>/static</c> — and ordinal, mirroring <see cref="Assimalign.Cohesion.Http.HttpPath"/>
    /// equality. An exact-prefix match yields an empty remainder (the mount root requested
    /// without a trailing slash).
    /// </summary>
    public static bool TryGetRelativePath(string path, string prefix, out string remainder)
    {
        if (prefix.Length == 0)
        {
            remainder = path;
            return true;
        }

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            remainder = string.Empty;
            return false;
        }

        if (path.Length == prefix.Length)
        {
            remainder = string.Empty;
            return true;
        }

        if (path[prefix.Length] == '/')
        {
            remainder = path[prefix.Length..];
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    /// <summary>
    /// Rejects request paths that could resolve outside the mounted root or address something
    /// other than a plain file. Transports percent-decode the request path before it reaches
    /// middleware (all but <c>%2F</c>), so encoded traversal like <c>%2e%2e</c> or <c>..%5C</c>
    /// arrives here as literal dot segments — this gate sees the same text a file system would.
    /// </summary>
    /// <remarks>
    /// Unsafe shapes: any NUL; any <c>:</c> (Windows drive roots and NTFS alternate data
    /// streams); any segment — split on both <c>/</c> and <c>\</c>, since
    /// <c>FileSystemPath</c> treats backslash as a separator — equal to <c>.</c> or <c>..</c>.
    /// <c>FileSystemPath.Parse</c> independently throws on interior dot segments and illegal
    /// characters; this check runs first so hostile requests get a deterministic <c>404</c>
    /// instead of exception-driven control flow.
    /// </remarks>
    public static bool HasUnsafeSegments(ReadOnlySpan<char> remainder)
    {
        if (remainder.ContainsAny('\0', ':'))
        {
            return true;
        }

        while (!remainder.IsEmpty)
        {
            int separator = remainder.IndexOfAny('/', '\\');
            ReadOnlySpan<char> segment = separator < 0 ? remainder : remainder[..separator];
            remainder = separator < 0 ? ReadOnlySpan<char>.Empty : remainder[(separator + 1)..];

            // "." and ".." are the only all-dot segments short enough to be dot segments.
            if (segment.Length is 1 or 2 && segment.IndexOfAnyExcept('.') < 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="path"/>, relative to the root of <paramref name="fileSystem"/>, to a
    /// file in that mount. The path passes the same two defense layers as a request path — the
    /// <see cref="HasUnsafeSegments"/> gate, then <see cref="FileSystemPath.Parse(string)"/> — before
    /// the mount is consulted, so no input can address anything outside the mount: dot segments,
    /// backslash traversal, drive and stream forms, and NUL are refused, and a leading <c>/</c> means
    /// the mount root rather than the host's.
    /// </summary>
    /// <param name="fileSystem">The mount to resolve in.</param>
    /// <param name="path">The mount-relative path, with or without a leading <c>/</c>.</param>
    /// <param name="file">When this method returns <see langword="true"/>, the resolved file.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="path"/> names a file in the mount;
    /// <see langword="false"/> when it is unsafe, unparseable, missing, or names a directory.
    /// </returns>
    public static bool TryResolveFile(IFileSystem fileSystem, string path, [NotNullWhen(true)] out IFileSystemFile? file)
    {
        file = null;

        if (HasUnsafeSegments(path))
        {
            return false;
        }

        FileSystemPath parsed;
        try
        {
            parsed = FileSystemPath.Parse(path);
        }
        catch (ArgumentException)
        {
            // FileSystemPath rejects interior dot segments and illegal path characters outright.
            return false;
        }

        if (TryGetInfo(fileSystem, parsed, out IFileSystemInfo? info) && info is IFileSystemFile resolved)
        {
            file = resolved;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Looks up <paramref name="path"/> in <paramref name="fileSystem"/>. The path must already have
    /// passed <see cref="HasUnsafeSegments"/> and <see cref="FileSystemPath.Parse(string)"/>; the mount
    /// root itself (an empty or <c>/</c> path) is never returned.
    /// </summary>
    /// <param name="fileSystem">The mount to look in.</param>
    /// <param name="path">The parsed, gate-checked path.</param>
    /// <param name="info">When this method returns <see langword="true"/>, the file or directory found.</param>
    /// <returns><see langword="true"/> when the mount holds an entry at <paramref name="path"/>.</returns>
    public static bool TryGetInfo(IFileSystem fileSystem, FileSystemPath path, [NotNullWhen(true)] out IFileSystemInfo? info)
    {
        info = null;

        // Lookups are mount-relative. Every provider merges a relative path against its own root,
        // but a leading '/' means "absolute in the provider's namespace": the mount root for the
        // in-memory provider, the drive root for the physical one, whose Merge then refuses the
        // path. Stripping it here is safe because the traversal gate and FileSystemPath.Parse have
        // already rejected dot segments.
        ReadOnlySpan<char> relative = path.AsSpan().TrimStart('/');
        if (relative.IsEmpty)
        {
            return false;
        }

        FileSystemPath mountPath = FileSystemPath.Parse(relative);
        try
        {
            if (!fileSystem.Exists(mountPath))
            {
                return false;
            }
            info = fileSystem.GetInfo(mountPath);
            return true;
        }
        catch (FileSystemException)
        {
            // A lookup race (deleted between Exists and GetInfo) or a mount-specific refusal:
            // either way the path is not servable.
            return false;
        }
    }
}
