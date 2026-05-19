using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable cookie collection synchronized with an
/// <see cref="IHttpHeaderCollection"/>. The collection parses the
/// configured header (<see cref="HttpHeaderKey.Cookie"/> on the request
/// side, <see cref="HttpHeaderKey.SetCookie"/> on the response side) at
/// construction and writes every subsequent mutation back to the same
/// header, so the wire-level header is always the source of truth.
/// </summary>
/// <remarks>
/// <para>
/// The request-side <c>Cookie</c> header (RFC 6265 §5.4) carries
/// semicolon-joined <c>name=value</c> pairs in a single header value.
/// The response-side <c>Set-Cookie</c> header (RFC 6265 §3) is
/// multi-valued; each cookie occupies its own value slot complete with
/// attributes (<c>Path</c>, <c>Domain</c>, <c>Expires</c>, <c>Max-Age</c>,
/// <c>Secure</c>, <c>HttpOnly</c>, <c>SameSite</c>). This collection
/// transparently handles both shapes based on which header key it was
/// constructed with.
/// </para>
/// <para>
/// Synchronization is one-way (collection &#8594; header). Direct
/// mutations to the underlying header collection after construction are
/// not reflected back into this object &#8211; callers should mutate
/// through the collection rather than the header.
/// </para>
/// </remarks>
public sealed class HttpCookieCollection : IHttpCookieCollection
{
    private readonly IHttpHeaderCollection _headers;
    private readonly HttpHeaderKey _headerKey;
    private readonly List<HttpCookie> _cookies = new();

    /// <summary>
    /// Initializes an empty in-memory cookie collection. Backed by a
    /// private <see cref="HttpHeaderCollection"/> so mutations have a
    /// place to land without being observed externally; this overload is
    /// intended for tests and factory code that builds a collection of
    /// cookies to attach later.
    /// </summary>
    public HttpCookieCollection()
        : this(new HttpHeaderCollection(), HttpHeaderKey.SetCookie)
    {
    }

    /// <summary>
    /// Initializes a cookie collection synchronized with
    /// <paramref name="headers"/> through the supplied
    /// <paramref name="headerKey"/>. Existing cookies on the header are
    /// parsed into the collection during construction; subsequent
    /// <see cref="Add"/>, <see cref="Remove"/>, and <see cref="Clear"/>
    /// calls write back to the same header.
    /// </summary>
    /// <param name="headers">The header collection that owns the wire
    /// representation.</param>
    /// <param name="headerKey">Either <see cref="HttpHeaderKey.Cookie"/>
    /// (request side) or <see cref="HttpHeaderKey.SetCookie"/> (response
    /// side). The cookie format follows the header semantics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="headers"/> is <see langword="null"/>.
    /// </exception>
    public HttpCookieCollection(IHttpHeaderCollection headers, HttpHeaderKey headerKey)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers = headers;
        _headerKey = headerKey;

        ReadFromHeaders();
    }

    /// <inheritdoc />
    public int Count => _cookies.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public void Add(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _cookies.Add(item);
        WriteToHeaders();
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cookies.Clear();
        _headers.Remove(_headerKey);
    }

    /// <inheritdoc />
    public bool Contains(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _cookies.Contains(item);
    }

    /// <inheritdoc />
    public void CopyTo(HttpCookie[] array, int arrayIndex) => _cookies.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public IEnumerator<HttpCookie> GetEnumerator() => _cookies.GetEnumerator();

    /// <inheritdoc />
    public bool Remove(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_cookies.Remove(item))
        {
            return false;
        }

        WriteToHeaders();
        return true;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void ReadFromHeaders()
    {
        if (!_headers.TryGetValue(_headerKey, out HttpHeaderValue value))
        {
            return;
        }

        if (_headerKey == HttpHeaderKey.SetCookie)
        {
            ParseSetCookieHeader(value);
        }
        else
        {
            ParseCookieHeader(value);
        }
    }

    private void WriteToHeaders()
    {
        if (_cookies.Count == 0)
        {
            _headers.Remove(_headerKey);
            return;
        }

        if (_headerKey == HttpHeaderKey.SetCookie)
        {
            // RFC 6265 §3 — each Set-Cookie occupies its own value (and
            // its own wire line). The transport's response writer
            // special-cases Set-Cookie to emit one line per value.
            string?[] values = _cookies.Select(c => (string?)c.ToString()).ToArray();
            _headers[_headerKey] = new HttpHeaderValue(values);
        }
        else
        {
            // RFC 6265 §5.4 — the Cookie header is a single value
            // carrying "; "-joined name=value pairs.
            string folded = string.Join("; ", _cookies.Select(c => c.Name + "=" + c.Value));
            _headers[_headerKey] = folded;
        }
    }

    private void ParseCookieHeader(HttpHeaderValue value)
    {
        // The Cookie header is logically a single value but may arrive as
        // multiple HttpHeaderValue slots when an HTTP/2 sender emits
        // separate cookie field lines (RFC 9113 §8.2.3 leaves coalescing
        // to the receiver). Walk all slots, splitting each on ';'.
        foreach (string? slot in value)
        {
            if (string.IsNullOrWhiteSpace(slot))
            {
                continue;
            }

            foreach (string segment in slot.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                ReadOnlySpan<char> trimmed = segment.AsSpan().Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                int eq = trimmed.IndexOf('=');
                string name;
                string val;
                if (eq < 0)
                {
                    name = trimmed.ToString();
                    val = string.Empty;
                }
                else
                {
                    name = trimmed[..eq].Trim().ToString();
                    val = trimmed[(eq + 1)..].Trim().ToString();
                }

                if (name.Length > 0)
                {
                    _cookies.Add(new HttpCookie(name, val));
                }
            }
        }
    }

    private void ParseSetCookieHeader(HttpHeaderValue value)
    {
        // Each Set-Cookie value is a full cookie definition with
        // attributes. We do NOT split on commas — the Expires attribute
        // is RFC 1123 formatted ("Wed, 09 Jun 2021 10:18:14 GMT") and
        // contains a comma that comma-folding would otherwise mangle.
        foreach (string? slot in value)
        {
            if (string.IsNullOrWhiteSpace(slot))
            {
                continue;
            }

            HttpCookie? cookie = ParseSetCookieValue(slot);
            if (cookie is not null)
            {
                _cookies.Add(cookie);
            }
        }
    }

    private static HttpCookie? ParseSetCookieValue(string raw)
    {
        string[] segments = raw.Split(';');
        if (segments.Length == 0)
        {
            return null;
        }

        string cookieSegment = segments[0].Trim();
        if (cookieSegment.Length == 0)
        {
            return null;
        }

        int eq = cookieSegment.IndexOf('=');
        string name;
        string value;
        if (eq < 0)
        {
            name = cookieSegment;
            value = string.Empty;
        }
        else
        {
            name = cookieSegment[..eq].Trim();
            value = cookieSegment[(eq + 1)..].Trim();
        }

        if (name.Length == 0)
        {
            return null;
        }

        // Build options with the wire-empty defaults: HttpCookieOptions's
        // own ctor sets Path = "/" as a convenience for senders, but for
        // a faithful round-trip we only set Path when the wire actually
        // carried it. Override here before walking the attributes.
        HttpCookieOptions options = new() { Path = null };

        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i].Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            int attrEq = segment.IndexOf('=');
            if (attrEq < 0)
            {
                ApplyFlagAttribute(segment, options);
            }
            else
            {
                string attrName = segment[..attrEq].Trim();
                string attrValue = segment[(attrEq + 1)..].Trim();
                ApplyValueAttribute(attrName, attrValue, options);
            }
        }

        return new HttpCookie(name, value, options);
    }

    private static void ApplyFlagAttribute(string segment, HttpCookieOptions options)
    {
        if (string.Equals(segment, "Secure", StringComparison.OrdinalIgnoreCase))
        {
            options.Secure = true;
        }
        else if (string.Equals(segment, "HttpOnly", StringComparison.OrdinalIgnoreCase))
        {
            options.HttpOnly = true;
        }
        else
        {
            // Preserve unknown flag attributes so a round-trip emits them
            // back on the wire (RFC 6265 §5.3 says unknown attributes are
            // ignored by the user agent, but a proxy / middleware that
            // re-emits the header should not silently drop them).
            options.Extensions.Add(segment);
        }
    }

    private static void ApplyValueAttribute(string name, string value, HttpCookieOptions options)
    {
        if (string.Equals(name, "Domain", StringComparison.OrdinalIgnoreCase))
        {
            options.Domain = value;
        }
        else if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
        {
            options.Path = value;
        }
        else if (string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase))
        {
            // RFC 1123 (preferred) and a handful of legacy formats that
            // user agents have historically tolerated. DateTimeOffset.TryParse
            // with InvariantCulture handles them all without bringing in a
            // bespoke date parser.
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset expires))
            {
                options.Expires = expires;
            }
            else
            {
                options.Extensions.Add(name + "=" + value);
            }
        }
        else if (string.Equals(name, "Max-Age", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            {
                options.MaxAge = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                options.Extensions.Add(name + "=" + value);
            }
        }
        else if (string.Equals(name, "SameSite", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse(value, ignoreCase: true, out HttpCookieSameSiteMode sameSite))
            {
                options.SameSite = sameSite;
            }
            else
            {
                options.Extensions.Add(name + "=" + value);
            }
        }
        else
        {
            options.Extensions.Add(name + "=" + value);
        }
    }
}
