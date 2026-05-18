using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable in-memory cookie collection. Used as the parsed
/// snapshot on the request side and as a general-purpose collection for
/// callers building cookies outside an HTTP exchange (tests, factories,
/// middleware that does its own buffering).
/// </summary>
/// <remarks>
/// This collection is pure storage &#8211; mutations are not propagated to
/// any header collection. Response-side cookie state that drains into
/// <c>Set-Cookie</c> headers uses an internal sync-aware collection owned by
/// the response cookie feature.
/// </remarks>
public sealed class HttpCookieCollection : IHttpCookieCollection
{
    private readonly IHttpHeaderCollection _headers;

    public HttpCookieCollection(IHttpHeaderCollection headers)
    {
        _headers = ArgumentNullException.ThrowIfNull<IHttpHeaderCollection>(headers);
    }


    /// <inheritdoc />
    public int Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public void Add(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _cookies.Add(item);
    }

    /// <inheritdoc />
    public void Clear() => _cookies.Clear();

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
        return _cookies.Remove(item);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
