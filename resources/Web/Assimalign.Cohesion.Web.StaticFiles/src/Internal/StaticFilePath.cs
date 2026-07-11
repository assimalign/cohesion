using System;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Path helpers for the static-files middleware: segment-aligned prefix matching and the
/// traversal gate that keeps every lookup inside the mounted file-system root.
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
}
