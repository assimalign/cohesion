using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Removes dot segments from a decoded request path (RFC 3986 §5.2.4) before a proxy routes or
/// forwards it, so the path a route matched is the path the destination receives.
/// </summary>
/// <remarks>
/// A server keeps an origin-form target's <c>..</c> segments, and decodes <c>%2E</c> to <c>.</c>, so
/// <c>/api/%2E%2E/admin</c> arrives as <c>/api/../admin</c>. Forwarded unchanged under a route for
/// <c>/api</c>, a destination that normalizes would serve <c>/admin</c>. The decoder leaves
/// <c>%2F</c> encoded, so a dot segment can still hide behind it (<c>/api/x%2F..%2F..%2Fadmin</c>),
/// and some destinations treat <c>\</c> as a separator: a path with a <c>.</c> or <c>..</c> between
/// such separators is refused rather than guessed at.
/// </remarks>
internal static class HttpProxyPath
{
    /// <summary>
    /// Normalizes <paramref name="path"/>, or returns <see langword="false"/> when it hides a dot
    /// segment behind an encoded slash or a backslash.
    /// </summary>
    public static bool TryNormalize(string? path, out string normalized)
    {
        if (string.IsNullOrEmpty(path) || path == "*")
        {
            // An asterisk-form OPTIONS target has no path to forward; the destination receives "/".
            normalized = "/";
            return true;
        }

        if (!NeedsWork(path))
        {
            normalized = path[0] == '/' ? path : "/" + path;
            return true;
        }

        string[] segments = path.Split('/');
        List<string> output = new(segments.Length);

        // A path that starts with '/' splits into an empty first segment, which the leading '/' of
        // the result restores; one that does not is treated as if it did.
        int first = path[0] == '/' ? 1 : 0;

        for (int index = first; index < segments.Length; index++)
        {
            string segment = segments[index];
            bool isLast = index == segments.Length - 1;

            if (segment == ".")
            {
                if (isLast)
                {
                    output.Add(string.Empty);
                }

                continue;
            }

            if (segment == "..")
            {
                if (output.Count > 0)
                {
                    output.RemoveAt(output.Count - 1);
                }

                if (isLast)
                {
                    output.Add(string.Empty);
                }

                continue;
            }

            if (HidesDotSegment(segment))
            {
                normalized = string.Empty;
                return false;
            }

            output.Add(segment);
        }

        normalized = "/" + string.Join('/', output);
        return true;
    }

    // Most paths have no '.' segment, no encoded slash and no backslash; they are returned as they are.
    private static bool NeedsWork(string path)
    {
        ReadOnlySpan<char> text = path;
        if (text.Contains('\\') || text.Contains("%2F", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (Range range in text.Split('/'))
        {
            if (text[range] is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HidesDotSegment(string segment)
    {
        ReadOnlySpan<char> text = segment;
        if (!text.Contains('\\') && !text.Contains("%2F", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        while (true)
        {
            int slash = text.IndexOf("%2F", StringComparison.OrdinalIgnoreCase);
            int backslash = text.IndexOf('\\');
            int end;
            int width;

            if (slash < 0 && backslash < 0)
            {
                return text is "." or "..";
            }

            if (slash >= 0 && (backslash < 0 || slash < backslash))
            {
                end = slash;
                width = 3;
            }
            else
            {
                end = backslash;
                width = 1;
            }

            ReadOnlySpan<char> piece = text[..end];
            if (piece is "." or "..")
            {
                return true;
            }

            text = text[(end + width)..];
        }
    }
}
