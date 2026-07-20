using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// Precompiles the configured eligible media types (<see cref="ResponseCompressionOptions.MimeTypes"/>)
/// into a fast request-time match over a response's <c>Content-Type</c>. Built once when
/// <c>UseResponseCompression</c> is called; the per-response test is allocation-free.
/// </summary>
/// <remarks>
/// A configured entry may be an exact media type (<c>application/json</c>), a subtype wildcard
/// (<c>text/*</c>), or the full wildcard (<c>*/*</c>). Matching is case-insensitive and ignores the
/// <c>Content-Type</c> parameters (for example <c>; charset=utf-8</c>).
/// </remarks>
internal sealed class CompressibleMimeMatcher
{
    private readonly HashSet<string> _exact;
    private readonly HashSet<string> _typeWildcards;
    private readonly bool _matchAny;

    private CompressibleMimeMatcher(HashSet<string> exact, HashSet<string> typeWildcards, bool matchAny)
    {
        _exact = exact;
        _typeWildcards = typeWildcards;
        _matchAny = matchAny;
    }

    /// <summary>
    /// Compiles a matcher from the configured media-type patterns.
    /// </summary>
    /// <param name="patterns">The configured eligible media types.</param>
    /// <returns>A compiled matcher.</returns>
    public static CompressibleMimeMatcher Create(IEnumerable<string> patterns)
    {
        HashSet<string> exact = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> typeWildcards = new(StringComparer.OrdinalIgnoreCase);
        bool matchAny = false;

        foreach (string raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string pattern = raw.Trim();
            if (pattern == "*/*" || pattern == "*")
            {
                matchAny = true;
            }
            else if (pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                typeWildcards.Add(pattern[..^2]);
            }
            else
            {
                exact.Add(pattern);
            }
        }

        return new CompressibleMimeMatcher(exact, typeWildcards, matchAny);
    }

    /// <summary>
    /// Determines whether a response <c>Content-Type</c> header value is eligible for compression.
    /// </summary>
    /// <param name="contentType">The raw <c>Content-Type</c> value, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the media type matches a configured pattern.</returns>
    public bool IsMatch(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        if (_matchAny)
        {
            return true;
        }

        ReadOnlySpan<char> mediaType = contentType.AsSpan();

        // Strip parameters: "application/json; charset=utf-8" -> "application/json".
        int semicolon = mediaType.IndexOf(';');
        if (semicolon >= 0)
        {
            mediaType = mediaType[..semicolon];
        }
        mediaType = mediaType.Trim();

        if (mediaType.IsEmpty)
        {
            return false;
        }

        string mediaTypeString = mediaType.ToString();
        if (_exact.Contains(mediaTypeString))
        {
            return true;
        }

        int slash = mediaType.IndexOf('/');
        if (slash > 0 && _typeWildcards.Count > 0)
        {
            return _typeWildcards.Contains(mediaType[..slash].ToString());
        }

        return false;
    }
}
