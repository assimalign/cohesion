using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.AotSample;

/// <summary>Resolves remote references from a fixed set of documents.</summary>
internal sealed class StaticResolver : IJsonSchemaResolver
{
    private readonly Dictionary<string, JsonElement> _documents = new(StringComparer.Ordinal);

    public StaticResolver Add(string uri, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        _documents[uri] = document.RootElement.Clone();
        return this;
    }

    public bool TryResolve(Uri uri, out JsonElement schema) => _documents.TryGetValue(uri.AbsoluteUri, out schema);
}
