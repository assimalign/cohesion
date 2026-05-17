using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a mutable cookie collection.
/// </summary>
public sealed class HttpCookieCollection : IHttpCookieCollection
{
    private readonly List<HttpCookie> _cookies = new();
    private readonly IHttpHeaderCollection _headers;

    internal HttpCookieCollection(IHttpHeaderCollection headers)
    {
        // Pass the reference to the header collection so that cookies can be removed from the
        // collection when the caller removes the corresponding header. This is necessary to keep
        // the cookie collection and the header collection in sync.
        ArgumentNullException.ThrowIfNull(headers);
        _headers = headers;
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
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cookies.Clear();
    }

    /// <inheritdoc />
    public bool Contains(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _cookies.Contains(item);
    }

    /// <inheritdoc />
    public void CopyTo(HttpCookie[] array, int arrayIndex)
    {
        _cookies.CopyTo(array, arrayIndex);
    }

    /// <inheritdoc />
    public IEnumerator<HttpCookie> GetEnumerator()
    {
        return _cookies.GetEnumerator();
    }

    /// <inheritdoc />
    public bool Remove(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _cookies.Remove(item);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
