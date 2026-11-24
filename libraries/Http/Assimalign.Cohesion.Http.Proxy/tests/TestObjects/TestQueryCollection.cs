using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// A query collection other than <see cref="HttpQueryCollection"/>: it exposes another collection's
/// parameters and reports whatever source text it was given.
/// </summary>
internal sealed class TestQueryCollection : IHttpQueryCollection
{
    private readonly IHttpQueryCollection _parameters;

    public TestQueryCollection(IHttpQueryCollection parameters, HttpQuery? source)
    {
        _parameters = parameters;
        Source = source;
    }

    public HttpQuery? Source { get; }

    public int Count => _parameters.Count;

    public HttpQueryValue this[HttpQueryKey key] => _parameters[key];

    public bool ContainsKey(HttpQueryKey key) => _parameters.ContainsKey(key);

    public bool TryGetValue(HttpQueryKey key, out HttpQueryValue value) => _parameters.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<HttpQueryKey, HttpQueryValue>> GetEnumerator() => _parameters.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
