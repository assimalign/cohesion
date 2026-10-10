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
    /// <para>
    /// Unsafe shapes: any NUL; any <c>:</c> (Windows drive roots and NTFS alternate data
    /// streams); any segment — split on both <c>/</c> and <c>\</c>, since
    /// <c>FileSystemPath</c> treats backslash as a separator — equal to <c>.</c> or <c>..</c>,
    /// or shaped like an 8.3 short-name alias (see <see cref="IsShortNameAlias"/>).
    /// <c>FileSystemPath.Parse</c> independently throws on interior dot segments and illegal
    /// characters; this check runs first so hostile requests get a deterministic <c>404</c>
    /// instead of exception-driven control flow.
    /// </para>
    /// <para>
    /// The alias check is not OS-gated: an in-memory mount answers the same on every host, and a
    /// non-Windows host still reaches short names through an SMB share or a FAT volume.
    /// </para>
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

            if (IsShortNameAlias(segment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reports whether <paramref name="segment"/> has the shape of a generated 8.3 short name: a stem
    /// of at most eight characters before the first <c>.</c> that contains <c>~</c> followed by a
    /// digit, such as <c>UPLOAD~1.HTM</c> or <c>PROGRA~1</c>.
    /// </summary>
    /// <remarks>
    /// A volume that generates short names (NTFS with 8.3 names on, FAT) opens a file through its
    /// alias, and the alias carries a different extension: <c>upload.htmlx</c> answers to
    /// <c>UPLOAD~1.HTM</c>. The content type is read from the name the request spells, so serving
    /// the alias would type an unmapped upload as <c>text/html</c> — the stored XSS the content-type
    /// gate exists to stop. Generated aliases always have this shape, and the segment is checked
    /// after the trailing dots and spaces Windows trims (<c>UPLOAD~1.HTM.</c>). A short name an
    /// administrator sets by hand (<c>fsutil file setshortname</c>) need not contain <c>~</c> and is
    /// not detected. A real file whose name has this shape, such as <c>report~1.txt</c>, is
    /// unservable; a longer stem, such as <c>photo~2023.png</c>, is not affected.
    /// </remarks>
    /// <param name="segment">One path segment, without separators.</param>
    /// <returns><see langword="true"/> when the segment could be a generated short-name alias.</returns>
    private static bool IsShortNameAlias(ReadOnlySpan<char> segment)
    {
        // Windows trims trailing dots and spaces before the lookup, so "UPLOAD~1.HTM." and
        // "UPLOAD~1 " open the same file as the alias itself.
        segment = segment.TrimEnd(". ");
        int dot = segment.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0 ? segment : segment[..dot];
        if (stem.Length > 8)
        {
            return false;
        }

        for (int i = 0; i < stem.Length - 1; i++)
        {
            if (stem[i] == '~' && char.IsAsciiDigit(stem[i + 1]))
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
    /// the mount root rather than the host's. An 8.3 short-name alias is refused too, so the name the
    /// caller spells is the name the file has, and the extension its type is read from is the file's.
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
