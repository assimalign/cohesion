using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;

/// <summary>
/// A response cookie feature installed ahead of the policy, standing in for a richer feature such as a
/// signing one. Its collection records every cookie it is asked to queue and stays synchronized with the
/// response's <c>Set-Cookie</c> header, so the policy must compose with it rather than replace it.
/// </summary>
internal sealed class RecordingResponseCookieFeature : IHttpResponseCookieFeature
{
    private readonly RecordingCookieCollection _cookies;

    public RecordingResponseCookieFeature(IHttpHeaderCollection headers)
    {
        _cookies = new RecordingCookieCollection(new HttpCookieCollection(headers, HttpHeaderKey.SetCookie));
    }

    public string Name => nameof(RecordingResponseCookieFeature);

    public IHttpCookieCollection Cookies => _cookies;

    /// <summary>Gets the cookies the collection was asked to queue, in order.</summary>
    public IReadOnlyList<HttpCookie> Added => _cookies.Added;

    private sealed class RecordingCookieCollection : IHttpCookieCollection
    {
        private readonly IHttpCookieCollection _inner;

        public RecordingCookieCollection(IHttpCookieCollection inner)
        {
            _inner = inner;
        }

        public List<HttpCookie> Added { get; } = [];

        public int Count => _inner.Count;

        public bool IsReadOnly => _inner.IsReadOnly;

        public void Add(HttpCookie item)
        {
            Added.Add(item);
            _inner.Add(item);
        }

        public void Clear() => _inner.Clear();

        public bool Contains(HttpCookie item) => _inner.Contains(item);

        public void CopyTo(HttpCookie[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);

        public IEnumerator<HttpCookie> GetEnumerator() => _inner.GetEnumerator();

        public bool Remove(HttpCookie item) => _inner.Remove(item);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
