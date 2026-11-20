using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// An RFC 3986 URI reference split into its five components, with reference resolution (§5.2) and
/// the normalization (§6.2.2, §6.2.3) that schema identification needs.
/// </summary>
/// <remarks>
/// <see cref="Uri"/> is not used for identification: it rewrites paths it believes are file paths,
/// unescapes characters, and resolves references differently from RFC 3986 on some platforms. This
/// type keeps every component as the text it was written in, except where normalization says
/// otherwise.
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
internal sealed class UriReference
{
    private const string unreservedPunctuation = "-._~";
    private const string fragmentSafe = "!$&'()*+,;=:@/?-._~";

    private UriReference(string? scheme, string? authority, string path, string? query, string? fragment)
    {
        Scheme = scheme;
        Authority = authority;
        Path = path;
        Query = query;
        Fragment = fragment;
    }

    /// <summary>Gets the scheme, lower-cased, or <see langword="null"/> for a relative reference.</summary>
    public string? Scheme { get; }

    /// <summary>Gets the authority, or <see langword="null"/> when the reference has none.</summary>
    public string? Authority { get; }

    /// <summary>Gets the path, possibly empty.</summary>
    public string Path { get; }

    /// <summary>Gets the query without its <c>?</c>, or <see langword="null"/>.</summary>
    public string? Query { get; }

    /// <summary>Gets the fragment without its <c>#</c>, still percent-encoded, or <see langword="null"/>.</summary>
    public string? Fragment { get; }

    /// <summary>Gets a value indicating whether the reference has a scheme.</summary>
    public bool IsAbsolute => Scheme is not null;

    /// <summary>
    /// Parses a URI reference using the component split of RFC 3986 Appendix B, rejecting a scheme
    /// that does not match <c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c>.
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out UriReference? reference)
    {
        reference = null;
        if (text is null)
        {
            return false;
        }

        int length = text.Length;
        int index = 0;
        string? scheme = null;
        int colon = -1;
        for (int i = 0; i < length; i++)
        {
            char c = text[i];
            if (c == ':')
            {
                colon = i;
                break;
            }

            if (c == '/' || c == '?' || c == '#')
            {
                break;
            }
        }

        if (colon == 0)
        {
            return false;
        }

        if (colon > 0)
        {
            if (!IsScheme(text.AsSpan(0, colon)))
            {
                return false;
            }

            scheme = text.Substring(0, colon).ToLowerInvariant();
            index = colon + 1;
        }

        string? authority = null;
        if (index + 1 < length && text[index] == '/' && text[index + 1] == '/')
        {
            int start = index + 2;
            int end = start;
            while (end < length && text[end] != '/' && text[end] != '?' && text[end] != '#')
            {
                end++;
            }

            authority = text.Substring(start, end - start);
            index = end;
        }

        int pathStart = index;
        while (index < length && text[index] != '?' && text[index] != '#')
        {
            index++;
        }

        string path = text.Substring(pathStart, index - pathStart);
        string? query = null;
        if (index < length && text[index] == '?')
        {
            int start = index + 1;
            while (index < length && text[index] != '#')
            {
                index++;
            }

            query = text.Substring(start, index - start);
        }

        string? fragment = null;
        if (index < length && text[index] == '#')
        {
            fragment = text.Substring(index + 1);
        }

        reference = new UriReference(scheme, authority, path, query, fragment);
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="reference"/> against this absolute base (RFC 3986 §5.2.2).
    /// </summary>
    public UriReference Resolve(UriReference reference)
    {
        if (reference.Scheme is not null)
        {
            return new UriReference(reference.Scheme, reference.Authority, RemoveDotSegments(reference.Path), reference.Query, reference.Fragment);
        }

        string? authority;
        string path;
        string? query;
        if (reference.Authority is not null)
        {
            authority = reference.Authority;
            path = RemoveDotSegments(reference.Path);
            query = reference.Query;
        }
        else
        {
            if (reference.Path.Length == 0)
            {
                path = Path;
                query = reference.Query ?? Query;
            }
            else
            {
                path = reference.Path[0] == '/'
                    ? RemoveDotSegments(reference.Path)
                    : RemoveDotSegments(Merge(reference.Path));
                query = reference.Query;
            }

            authority = Authority;
        }

        return new UriReference(Scheme, authority, path, query, reference.Fragment);
    }

    /// <summary>Returns a copy with the given fragment (or none).</summary>
    public UriReference WithFragment(string? fragment) =>
        string.Equals(fragment, Fragment, StringComparison.Ordinal) ? this : new UriReference(Scheme, Authority, Path, Query, fragment);

    /// <summary>
    /// Returns the normalized text of this absolute reference, used as an identity key: a lower-case
    /// host, no default port, upper-case percent-encodings, unreserved characters decoded, dot
    /// segments removed, an empty http(s) path written as <c>/</c>, and no empty fragment.
    /// </summary>
    public string ToNormalizedString(bool includeFragment = true)
    {
        StringBuilder builder = new();
        if (Scheme is not null)
        {
            builder.Append(Scheme).Append(':');
        }

        string path = NormalizePercentEncoding(Path);
        if (Authority is not null)
        {
            builder.Append("//").Append(NormalizeAuthority(Authority, Scheme));
            if (path.Length == 0 && (Scheme is "http" or "https" or "ws" or "wss"))
            {
                path = "/";
            }
        }

        builder.Append(Scheme is not null ? RemoveDotSegments(path) : path);
        if (Query is not null)
        {
            builder.Append('?').Append(NormalizePercentEncoding(Query));
        }

        if (includeFragment && !string.IsNullOrEmpty(Fragment))
        {
            builder.Append('#').Append(NormalizePercentEncoding(Fragment));
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        StringBuilder builder = new();
        if (Scheme is not null)
        {
            builder.Append(Scheme).Append(':');
        }

        if (Authority is not null)
        {
            builder.Append("//").Append(Authority);
        }

        builder.Append(Path);
        if (Query is not null)
        {
            builder.Append('?').Append(Query);
        }

        if (Fragment is not null)
        {
            builder.Append('#').Append(Fragment);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Percent-decodes text as UTF-8. Returns <see langword="null"/> for a malformed escape or bytes
    /// that are not valid UTF-8.
    /// </summary>
    public static string? PercentDecode(ReadOnlySpan<char> text)
    {
        if (text.IndexOf('%') < 0)
        {
            return text.ToString();
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '%')
                {
                    if (i + 2 >= text.Length || !IsHex(text[i + 1]) || !IsHex(text[i + 2]))
                    {
                        return null;
                    }

                    rented[count++] = (byte)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2]));
                    i += 2;
                }
                else
                {
                    count += Encoding.UTF8.GetBytes(text.Slice(i, char.IsHighSurrogate(c) && i + 1 < text.Length ? 2 : 1), rented.AsSpan(count));
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length)
                    {
                        i++;
                    }
                }
            }

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(rented, 0, count);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Percent-encodes, as UTF-8, every character a URI fragment cannot carry literally
    /// (RFC 3986 §3.5: <c>fragment = *( pchar / "/" / "?" )</c>). An existing <c>%</c> is encoded too,
    /// because a JSON Pointer token may contain one.
    /// </summary>
    public static string EscapeFragment(string text)
    {
        int i = 0;
        while (i < text.Length && IsFragmentSafe(text[i]))
        {
            i++;
        }

        if (i == text.Length)
        {
            return text;
        }

        StringBuilder builder = new(text.Length + 16);
        builder.Append(text, 0, i);
        Span<byte> buffer = stackalloc byte[4];
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (IsFragmentSafe(c))
            {
                builder.Append(c);
                continue;
            }

            int width = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            int written = Encoding.UTF8.GetBytes(text.AsSpan(i, width), buffer);
            for (int b = 0; b < written; b++)
            {
                builder.Append('%').Append("0123456789ABCDEF"[buffer[b] >> 4]).Append("0123456789ABCDEF"[buffer[b] & 0xF]);
            }

            i += width - 1;
        }

        return builder.ToString();
    }

    private static bool IsFragmentSafe(char c) =>
        char.IsAsciiLetterOrDigit(c) || fragmentSafe.Contains(c);

    private static bool IsScheme(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    private string Merge(string referencePath)
    {
        if (Authority is not null && Path.Length == 0)
        {
            return "/" + referencePath;
        }

        int slash = Path.LastIndexOf('/');
        return slash < 0 ? referencePath : string.Concat(Path.AsSpan(0, slash + 1), referencePath);
    }

    /// <summary>RFC 3986 §5.2.4.</summary>
    internal static string RemoveDotSegments(string path)
    {
        if (path.IndexOf('.') < 0)
        {
            return path;
        }

        ReadOnlySpan<char> input = path.AsSpan();
        StringBuilder output = new(path.Length);
        while (input.Length > 0)
        {
            if (input.StartsWith("../"))
            {
                input = input.Slice(3);
            }
            else if (input.StartsWith("./"))
            {
                input = input.Slice(2);
            }
            else if (input.StartsWith("/./"))
            {
                input = input.Slice(2);
            }
            else if (input.SequenceEqual("/."))
            {
                input = "/";
            }
            else if (input.StartsWith("/../"))
            {
                input = input.Slice(3);
                RemoveLastSegment(output);
            }
            else if (input.SequenceEqual("/.."))
            {
                input = "/";
                RemoveLastSegment(output);
            }
            else if (input.SequenceEqual(".") || input.SequenceEqual(".."))
            {
                input = ReadOnlySpan<char>.Empty;
            }
            else
            {
                int start = input[0] == '/' ? 1 : 0;
                int next = input.Slice(start).IndexOf('/');
                int end = next < 0 ? input.Length : start + next;
                output.Append(input.Slice(0, end));
                input = input.Slice(end);
            }
        }

        return output.ToString();
    }

    private static void RemoveLastSegment(StringBuilder output)
    {
        int index = output.Length - 1;
        while (index >= 0 && output[index] != '/')
        {
            index--;
        }

        output.Length = Math.Max(index, 0);
    }

    private static string NormalizeAuthority(string authority, string? scheme)
    {
        int at = authority.LastIndexOf('@');
        string userInfo = at >= 0 ? authority.Substring(0, at + 1) : string.Empty;
        string hostPort = at >= 0 ? authority.Substring(at + 1) : authority;
        string host = hostPort;
        string? port = null;
        int portColon = hostPort.LastIndexOf(':');
        if (portColon >= 0 && hostPort.IndexOf(']', portColon) < 0)
        {
            host = hostPort.Substring(0, portColon);
            port = hostPort.Substring(portColon + 1);
        }

        host = NormalizePercentEncoding(host).ToLowerInvariant();
        if (port is not null
            && (port.Length == 0
                || (port == "80" && scheme is "http" or "ws")
                || (port == "443" && scheme is "https" or "wss")))
        {
            port = null;
        }

        return port is null ? userInfo + host : userInfo + host + ":" + port;
    }

    private static string NormalizePercentEncoding(string text)
    {
        if (text.IndexOf('%') < 0)
        {
            return text;
        }

        StringBuilder builder = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '%' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                int value = (HexValue(text[i + 1]) << 4) | HexValue(text[i + 2]);
                char decoded = (char)value;
                if (value < 0x80 && (char.IsAsciiLetterOrDigit(decoded) || unreservedPunctuation.Contains(decoded)))
                {
                    builder.Append(decoded);
                }
                else
                {
                    builder.Append('%').Append(char.ToUpperInvariant(text[i + 1])).Append(char.ToUpperInvariant(text[i + 2]));
                }

                i += 2;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
