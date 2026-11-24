using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// A wrapper over another query collection that does not declare <see cref="IHttpQueryCollection.Source"/>,
/// so it exercises the interface's default.
/// </summary>
internal sealed class TestQueryCollection : IHttpQueryCollection
{
    private readonly IHttpQueryCollection _inner;

    public TestQueryCollection(IHttpQueryCollection inner)
    {
        _inner = inner;
    }

    public int Count => _inner.Count;

    public HttpQueryValue this[HttpQueryKey key] => _inner[key];

    public bool ContainsKey(HttpQueryKey key) => _inner.ContainsKey(key);

    public bool TryGetValue(HttpQueryKey key, out HttpQueryValue value) => _inner.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<HttpQueryKey, HttpQueryValue>> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
