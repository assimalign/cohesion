using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Parses the RFC 9110 &#167; 12.5 content-negotiation request headers &#8212; <c>Accept</c>,
/// <c>Accept-Charset</c>, <c>Accept-Encoding</c>, and <c>Accept-Language</c> &#8212; into
/// quality-weighted candidate lists. The returned lists are ordered by client preference:
/// descending quality, and for equal quality the more specific candidate first (RFC 9110
/// &#167; 12.4.2 / &#167; 12.5.1 &#8212; specificity breaks q ties).
/// </summary>
/// <remarks>
/// Parsing is deliberately tolerant: a malformed comma-separated segment (a bad token, a
/// missing <c>type/subtype</c>, or an unparseable <c>q</c> weight) is skipped rather than
/// throwing, so one broken entry never discards the whole header.
/// </remarks>
public static class HttpAcceptParser
{
    private enum QualityScan
    {
        None,
        Valid,
        Malformed,
    }

    /// <summary>
    /// Parses an <c>Accept</c> header value into media ranges with quality weights, ordered
    /// most-preferred first.
    /// </summary>
    /// <param name="value">The raw <c>Accept</c> header value, or <see langword="null"/>.</param>
    /// <returns>The parsed, preference-ordered media ranges; empty when the value is null or blank.</returns>
    public static IReadOnlyList<HttpMediaTypeQuality> ParseAccept(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<HttpMediaTypeQuality>();
        }

        var results = new List<HttpMediaTypeQuality>();
        ReadOnlySpan<char> span = value;

        while (!span.IsEmpty)
        {
            int comma = HttpFieldSyntax.IndexOfUnquoted(span, ',');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(comma < 0 ? span : span[..comma]);
            span = comma < 0 ? ReadOnlySpan<char>.Empty : span[(comma + 1)..];

            if (segment.IsEmpty)
            {
                continue;
            }

            int boundary = FindMediaRangeBoundary(segment, out QualityScan scan, out HttpQuality quality);
            if (scan == QualityScan.Malformed)
            {
                continue;
            }

            if (HttpMediaType.TryParse(segment[..boundary], out HttpMediaType mediaType))
            {
                results.Add(new HttpMediaTypeQuality(mediaType, quality));
            }
        }

        return results
            .OrderByDescending(entry => entry.Quality.PerMille)
            .ThenByDescending(entry => entry.MediaType.Specificity)
            .ToArray();
    }

    /// <summary>
    /// Parses an <c>Accept-Charset</c> header value (RFC 9110 &#167; 12.5.2) into weighted
    /// charset tokens, ordered most-preferred first.
    /// </summary>
    /// <param name="value">The raw <c>Accept-Charset</c> header value, or <see langword="null"/>.</param>
    /// <returns>The parsed, preference-ordered charsets; empty when the value is null or blank.</returns>
    public static IReadOnlyList<HttpQualityValue> ParseAcceptCharset(string? value)
        => ParseTokenList(value);

    /// <summary>
    /// Parses an <c>Accept-Encoding</c> header value (RFC 9110 &#167; 12.5.3) into weighted
    /// content-coding tokens, ordered most-preferred first. The <c>identity</c> coding and the
    /// <c>*</c> wildcard are returned verbatim when present; their special semantics are applied
    /// by <see cref="HttpContentNegotiation.TrySelectEncoding"/>.
    /// </summary>
    /// <param name="value">The raw <c>Accept-Encoding</c> header value, or <see langword="null"/>.</param>
    /// <returns>The parsed, preference-ordered content-codings; empty when the value is null or blank.</returns>
    public static IReadOnlyList<HttpQualityValue> ParseAcceptEncoding(string? value)
        => ParseTokenList(value);

    /// <summary>
    /// Parses an <c>Accept-Language</c> header value (RFC 9110 &#167; 12.5.4) into weighted
    /// language ranges, ordered most-preferred first.
    /// </summary>
    /// <param name="value">The raw <c>Accept-Language</c> header value, or <see langword="null"/>.</param>
    /// <returns>The parsed, preference-ordered language ranges; empty when the value is null or blank.</returns>
    public static IReadOnlyList<HttpQualityValue> ParseAcceptLanguage(string? value)
        => ParseTokenList(value);

    private static IReadOnlyList<HttpQualityValue> ParseTokenList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<HttpQualityValue>();
        }

        var results = new List<HttpQualityValue>();
        ReadOnlySpan<char> span = value;

        while (!span.IsEmpty)
        {
            int comma = HttpFieldSyntax.IndexOfUnquoted(span, ',');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(comma < 0 ? span : span[..comma]);
            span = comma < 0 ? ReadOnlySpan<char>.Empty : span[(comma + 1)..];

            if (segment.IsEmpty)
            {
                continue;
            }

            int semicolon = HttpFieldSyntax.IndexOfUnquoted(segment, ';');
            ReadOnlySpan<char> tokenSpan = HttpFieldSyntax.TrimOws(semicolon < 0 ? segment : segment[..semicolon]);
            if (!HttpFieldSyntax.IsToken(tokenSpan))
            {
                continue;
            }

            HttpQuality quality = HttpQuality.One;
            if (semicolon >= 0)
            {
                QualityScan scan = ScanQuality(segment[(semicolon + 1)..], out quality);
                if (scan == QualityScan.Malformed)
                {
                    continue;
                }
            }

            results.Add(new HttpQualityValue(tokenSpan.ToString(), quality));
        }

        return results
            .OrderByDescending(entry => entry.Quality.PerMille)
            .ThenBy(entry => entry.IsWildcard)
            .ToArray();
    }

    /// <summary>
    /// Locates the end of the media-range portion of an <c>Accept</c> segment (everything before
    /// the <c>q</c> weight) and extracts the quality. Parameters before <c>q</c> belong to the
    /// media type; the <c>q</c> weight and any accept-ext parameters after it are excluded.
    /// </summary>
    private static int FindMediaRangeBoundary(ReadOnlySpan<char> segment, out QualityScan scan, out HttpQuality quality)
    {
        scan = QualityScan.None;
        quality = HttpQuality.One;

        int firstSemicolon = HttpFieldSyntax.IndexOfUnquoted(segment, ';');
        if (firstSemicolon < 0)
        {
            return segment.Length;
        }

        int paramStart = firstSemicolon + 1;
        while (paramStart <= segment.Length)
        {
            ReadOnlySpan<char> rest = segment[paramStart..];
            int semicolon = HttpFieldSyntax.IndexOfUnquoted(rest, ';');
            ReadOnlySpan<char> paramSegment = HttpFieldSyntax.TrimOws(semicolon < 0 ? rest : rest[..semicolon]);

            int equals = HttpFieldSyntax.IndexOfUnquoted(paramSegment, '=');
            if (equals > 0)
            {
                ReadOnlySpan<char> name = HttpFieldSyntax.TrimOws(paramSegment[..equals]);
                if (name.Length == 1 && (name[0] == 'q' || name[0] == 'Q'))
                {
                    ReadOnlySpan<char> rawQuality = HttpFieldSyntax.TrimOws(paramSegment[(equals + 1)..]);
                    scan = HttpQuality.TryParse(rawQuality, out quality) ? QualityScan.Valid : QualityScan.Malformed;
                    return paramStart - 1;
                }
            }

            if (semicolon < 0)
            {
                break;
            }
            paramStart += semicolon + 1;
        }

        return segment.Length;
    }

    /// <summary>
    /// Scans the parameter portion of a token-list segment for the first <c>q</c> weight.
    /// </summary>
    private static QualityScan ScanQuality(ReadOnlySpan<char> parametersPortion, out HttpQuality quality)
    {
        quality = HttpQuality.One;

        while (!parametersPortion.IsEmpty)
        {
            int semicolon = HttpFieldSyntax.IndexOfUnquoted(parametersPortion, ';');
            ReadOnlySpan<char> paramSegment = HttpFieldSyntax.TrimOws(semicolon < 0 ? parametersPortion : parametersPortion[..semicolon]);

            int equals = HttpFieldSyntax.IndexOfUnquoted(paramSegment, '=');
            if (equals > 0)
            {
                ReadOnlySpan<char> name = HttpFieldSyntax.TrimOws(paramSegment[..equals]);
                if (name.Length == 1 && (name[0] == 'q' || name[0] == 'Q'))
                {
                    ReadOnlySpan<char> rawQuality = HttpFieldSyntax.TrimOws(paramSegment[(equals + 1)..]);
                    return HttpQuality.TryParse(rawQuality, out quality) ? QualityScan.Valid : QualityScan.Malformed;
                }
            }

            if (semicolon < 0)
            {
                break;
            }
            parametersPortion = parametersPortion[(semicolon + 1)..];
        }

        return QualityScan.None;
    }
}
