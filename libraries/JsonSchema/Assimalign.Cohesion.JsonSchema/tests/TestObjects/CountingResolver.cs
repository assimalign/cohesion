using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// A resolver over a fixed set of documents that counts how often it is asked.
/// </summary>
internal sealed class CountingResolver : IJsonSchemaResolver
{
    private readonly Dictionary<string, JsonElement> _documents = new(StringComparer.Ordinal);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public CountingResolver Add(string uri, string json)
    {
        _documents[uri] = TestJson.Parse(json);
        return this;
    }

    public bool TryResolve(Uri uri, out JsonElement schema)
    {
        Interlocked.Increment(ref _calls);
        return _documents.TryGetValue(uri.AbsoluteUri, out schema);
    }
}
