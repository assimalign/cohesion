using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// URL text for the rewrite engine: percent-encoding for each part of a URL, the re-encoded query the
/// pattern rules match and redirects carry, and redirect <c>Location</c> construction.
/// </summary>
/// <remarks>
/// <para>
/// The request's path is percent-decoded by every transport, and its raw query string is not carried on
/// <see cref="IHttpRequest"/>. Rewrite targets are therefore built as URL text (a template's literal text
/// as written, a capture percent-encoded for the part it lands in) and parsed back the way the transports
/// parse a request target: <see cref="HttpPath.FromUriComponent(string)"/> for the path and
/// <see cref="HttpQuery.Parse"/> for the query. Every value is decoded exactly once.
/// </para>
/// <para>
/// A redirect <c>Location</c> is written percent-encoded, which the Web.HttpsPolicy and Web.StaticFiles
/// redirects do not do for the path. A shared redirect helper is an open item in the Web area decisions
/// (ADR 1, "To revisit").
/// </para>
/// </remarks>
internal static class RewriteUrl
{
    // RFC 3986 §2.3.
    private const string unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    // RFC 3986 §2.2.
    private const string subDelimiters = "!$&'()*+,;=";

    private static ReadOnlySpan<char> HexDigits => "0123456789ABCDEF";

    // A path: pchar (RFC 3986 §3.3: unreserved, sub-delims, ':' and '@') and the '/' between segments.
    private static readonly SearchValues<char> _pathCharacters = SearchValues.Create(unreserved + subDelimiters + ":@/");

    // A query or fragment value, escaped the way Uri.EscapeDataString escapes it: everything but unreserved.
    private static readonly SearchValues<char> _componentCharacters = SearchValues.Create(unreserved);

    // What a template's literal query or fragment text may carry as written (RFC 3986 §3.4, §3.5).
    private static readonly SearchValues<char> _queryLiteralCharacters = SearchValues.Create(unreserved + subDelimiters + ":@/?");

    // What a template's literal authority may carry as written (RFC 3986 §3.2): userinfo, host and port,
    // including a bracketed IP literal.
    private static readonly SearchValues<char> _authorityLiteralCharacters = SearchValues.Create(unreserved + subDelimiters + ":@[]");

    // A request host a canonicalization redirect may echo: a DNS name, an IPv4 literal, or an IPv6 literal
    // without its brackets. Anything else (a '/', '@', '\' or '?' an attacker put in Host) is not echoed.
    private static readonly SearchValues<char> _hostCharacters = SearchValues.Create(unreserved + ":");

    /// <summary>
    /// Gets the characters a path carries unescaped.
    /// </summary>
    public static SearchValues<char> PathCharacters => _pathCharacters;

    /// <summary>
    /// Gets the characters a query or fragment value carries unescaped.
    /// </summary>
    public static SearchValues<char> ComponentCharacters => _componentCharacters;

    /// <summary>
    /// Gets the characters a template's literal query or fragment text carries unescaped.
    /// </summary>
    public static SearchValues<char> QueryLiteralCharacters => _queryLiteralCharacters;

    /// <summary>
    /// Gets the characters a template's literal authority carries unescaped.
    /// </summary>
    public static SearchValues<char> AuthorityLiteralCharacters => _authorityLiteralCharacters;

    /// <summary>
    /// Appends <paramref name="text"/>, percent-encoding (UTF-8) every character outside <paramref name="allowed"/>.
    /// </summary>
    public static void AppendEscaped(StringBuilder builder, ReadOnlySpan<char> text, SearchValues<char> allowed)
    {
        while (!text.IsEmpty)
        {
            int index = text.IndexOfAnyExcept(allowed);
            if (index < 0)
            {
                builder.Append(text);
                return;
            }

            builder.Append(text[..index]);
            text = text[index..];

            // A lone surrogate is not text; it is encoded as U+FFFD, as UTF-8 encoders do.
            if (Rune.DecodeFromUtf16(text, out Rune rune, out int consumed) != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = Math.Max(consumed, 1);
            }

            AppendPercentEncoded(builder, rune);
            text = text[consumed..];
        }
    }

    /// <summary>
    /// Returns a template's literal text as URL text: valid percent-encoded triplets are kept as written, a
    /// <c>%</c> that starts none becomes <c>%25</c>, and every other character outside
    /// <paramref name="allowed"/> is percent-encoded (UTF-8).
    /// </summary>
    public static string NormalizeLiteral(string literal, SearchValues<char> allowed)
    {
        ReadOnlySpan<char> text = literal;
        if (!text.ContainsAnyExcept(allowed))
        {
            return literal;
        }

        StringBuilder builder = new(literal.Length + 8);

        while (!text.IsEmpty)
        {
            int index = text.IndexOfAnyExcept(allowed);
            if (index < 0)
            {
                builder.Append(text);
                break;
            }

            builder.Append(text[..index]);
            text = text[index..];

            if (text[0] == '%')
            {
                if (text.Length >= 3 && char.IsAsciiHexDigit(text[1]) && char.IsAsciiHexDigit(text[2]))
                {
                    builder.Append(text[..3]);
                    text = text[3..];
                }
                else
                {
                    builder.Append("%25");
                    text = text[1..];
                }

                continue;
            }

            if (Rune.DecodeFromUtf16(text, out Rune rune, out int consumed) != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = Math.Max(consumed, 1);
            }

            AppendPercentEncoded(builder, rune);
            text = text[consumed..];
        }

        return builder.ToString();
    }

    /// <summary>
    /// Serializes a parsed query the way the pattern rules match it and redirects carry it: each key and
    /// value percent-encoded, joined with <c>&amp;</c> in enumeration order, and a key with an empty value
    /// written without <c>=</c>. Empty when the query has no entries.
    /// </summary>
    public static string SerializeQuery(IHttpQueryCollection query)
    {
        if (query.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(32);

        foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> entry in query)
        {
            if (builder.Length != 0)
            {
                builder.Append('&');
            }

            AppendEscaped(builder, entry.Key.Value, _componentCharacters);

            string value = entry.Value.Value;
            if (value.Length != 0)
            {
                builder.Append('=');
                AppendEscaped(builder, value, _componentCharacters);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses percent-encoded path text the way the transports parse a request target's path, decoding it
    /// once. Fails, rather than throwing, for a path no request may carry: one that decodes to a space,
    /// <c>?</c>, <c>#</c>, CR, LF, tab or NUL.
    /// </summary>
    public static bool TryParsePath(string text, out HttpPath path)
    {
        try
        {
            path = HttpPath.FromUriComponent(text);
            return true;
        }
        catch (HttpException)
        {
            // A decoded character HttpPath rejects.
        }
        catch (InvalidOperationException)
        {
            // A decoded NUL, which the shared URL decoder refuses.
        }

        path = default;
        return false;
    }

    /// <summary>
    /// Parses percent-encoded query text the way the transports parse a request's query. Fails, rather than
    /// throwing, for an entry with an empty key (<c>=value</c>), which no query key may be.
    /// </summary>
    public static bool TryParseQuery(string text, out IHttpQueryCollection query)
    {
        try
        {
            HttpQueryCollection parsed = new HttpQuery(text).Parse();
            parsed.IsReadOnly = true;
            query = parsed;
            return true;
        }
        catch (ArgumentException)
        {
            query = null!;
            return false;
        }
    }

    /// <summary>
    /// Determines whether a request host may be echoed into a redirect: a non-empty name or IP literal with
    /// no character that could change where the URL points.
    /// </summary>
    public static bool IsSafeHostName(ReadOnlySpan<char> host) => !host.IsEmpty && !host.ContainsAnyExcept(_hostCharacters);

    /// <summary>
    /// Formats a URL authority from a host component and an optional port, re-bracketing an IPv6 literal.
    /// </summary>
    public static string FormatAuthority(ReadOnlySpan<char> host, int? port)
    {
        string text = host.Contains(':') ? string.Concat("[", host, "]") : host.ToString();

        return port is int value
            ? string.Concat(text, ":", value.ToString(CultureInfo.InvariantCulture))
            : text;
    }

    /// <summary>
    /// Builds a redirect <c>Location</c> from percent-decoded values: the scheme and authority when given
    /// (an absolute URL), then the path base and path percent-encoded, then the re-encoded query.
    /// </summary>
    /// <param name="scheme">The scheme of an absolute URL, or <see langword="null"/> for an absolute path.</param>
    /// <param name="authority">The authority of an absolute URL.</param>
    /// <param name="pathBase">The path base the path is below.</param>
    /// <param name="path">The percent-decoded path below <paramref name="pathBase"/>.</param>
    /// <param name="query">The re-encoded query (<see cref="SerializeQuery"/>), empty for none.</param>
    public static string BuildLocation(string? scheme, string? authority, HttpPath pathBase, HttpPath path, string query)
    {
        StringBuilder builder = new(64);

        if (scheme is not null)
        {
            builder.Append(scheme).Append("://").Append(authority);
        }

        AppendPath(builder, pathBase, path.Value, encoded: false, relative: scheme is null);

        if (query.Length != 0)
        {
            builder.Append('?').Append(query);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds a redirect <c>Location</c> from a template's expansion, which is URL text already.
    /// </summary>
    /// <param name="pathBase">The path base a path target is below; an absolute target ignores it.</param>
    /// <param name="target">The expanded target.</param>
    /// <param name="query">
    /// The re-encoded query to write: the target's own when it has a <c>?</c> (empty removes the query), the
    /// request's otherwise.
    /// </param>
    public static string BuildLocation(HttpPath pathBase, RewriteTarget target, string query)
    {
        StringBuilder builder = new(64);

        if (target.Scheme is not null)
        {
            builder.Append(target.Scheme).Append("://").Append(target.Authority);

            // An absolute target names its own path: the path base of a branch does not apply.
            builder.Append(target.Path);
        }
        else
        {
            AppendPath(builder, pathBase, target.Path, encoded: true, relative: true);
        }

        if (query.Length != 0)
        {
            builder.Append('?').Append(query);
        }

        if (target.Fragment is not null)
        {
            builder.Append('#').Append(target.Fragment);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Validates a <c>Location</c> a delegate rule supplies: non-empty, with no control character or space.
    /// </summary>
    public static void ValidateLocation(string location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);

        foreach (char character in location)
        {
            if (character <= ' ' || character == '\u007F')
            {
                throw new ArgumentException(
                    "A redirect location cannot contain a control character or a space; percent-encode it.",
                    nameof(location));
            }
        }
    }

    // Appends the path base (outside the root) and the path. A relative Location that starts with "//" is a
    // network-path reference whose first segment a client reads as a host, so with no base to precede it the
    // leading slashes collapse to one and the redirect stays on this origin.
    private static void AppendPath(StringBuilder builder, HttpPath pathBase, ReadOnlySpan<char> path, bool encoded, bool relative)
    {
        ReadOnlySpan<char> prefix = pathBase.Value is { Length: > 1 } value ? value.AsSpan().TrimEnd('/') : default;

        if (!prefix.IsEmpty)
        {
            AppendEscaped(builder, prefix, _pathCharacters);
        }
        else if (relative && path.Length > 1 && path[0] == '/' && path[1] == '/')
        {
            builder.Append('/');
            path = path.TrimStart('/');
        }

        if (encoded)
        {
            builder.Append(path);
        }
        else
        {
            AppendEscaped(builder, path, _pathCharacters);
        }
    }

    private static void AppendPercentEncoded(StringBuilder builder, Rune rune)
    {
        Span<byte> utf8 = stackalloc byte[4];
        int length = rune.EncodeToUtf8(utf8);

        for (int i = 0; i < length; i++)
        {
            byte value = utf8[i];
            builder.Append('%').Append(HexDigits[value >> 4]).Append(HexDigits[value & 0xF]);
        }
    }
}
