using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Response-side cookie collection. Backed by an in-memory list so
/// <see cref="Count"/>, <see cref="Contains"/>, <see cref="Remove"/>, and
/// enumeration are O(1)/O(n) without re-parsing wire strings. Every
/// mutation also writes through to <c>response.Headers[Set-Cookie]</c>
/// so the transport layer can drain cookies into the wire response by
/// iterating headers alone &#8211; without taking a dependency on the
/// cookies package's typed model.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Set-Cookie</c> header is multi-valued (RFC 6265 §3) &#8211;
/// each cookie occupies its own value slot in
/// <see cref="HttpHeaderValue"/>. The transport's response writer is
/// expected to special-case <c>Set-Cookie</c> and emit one wire line per
/// value rather than comma-folding (RFC 6265 §3 forbids comma folding
/// for <c>Set-Cookie</c>).
/// </para>
/// <para>
/// Synchronization is one-way (collection &#8594; header). Direct
/// mutations to the underlying header collection are not reflected back
/// into this object; callers should mutate through the collection rather
/// than the header.
/// </para>
/// </remarks>
internal sealed class HttpResponseCookies : IHttpCookieCollection
{
    private readonly List<HttpCookie> _cookies = new();
    private readonly IHttpHeaderCollection _headers;

    public HttpResponseCookies(IHttpHeaderCollection headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers = headers;
    }

    public int Count => _cookies.Count;

    public bool IsReadOnly => false;

    public void Add(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _cookies.Add(item);

        HttpHeaderValue existing = _headers[HttpHeaderKey.SetCookie];
        _headers[HttpHeaderKey.SetCookie] = HttpHeaderValue.Concat(existing, item.ToString());
    }

    public void Clear()
    {
        _cookies.Clear();
        _headers.Remove(HttpHeaderKey.SetCookie);
    }

    public bool Contains(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _cookies.Contains(item);
    }

    public void CopyTo(HttpCookie[] array, int arrayIndex) => _cookies.CopyTo(array, arrayIndex);

    public IEnumerator<HttpCookie> GetEnumerator() => _cookies.GetEnumerator();

    public bool Remove(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_cookies.Remove(item))
        {
            return false;
        }

        RebuildHeader();
        return true;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void RebuildHeader()
    {
        if (_cookies.Count == 0)
        {
            _headers.Remove(HttpHeaderKey.SetCookie);
            return;
        }

        string?[] values = _cookies.Select(c => (string?)c.ToString()).ToArray();
        _headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(values);
    }
}
