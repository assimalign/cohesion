using System;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// Supplies schema documents the registry does not hold yet, such as the target of a remote
/// <c>$ref</c>.
/// </summary>
/// <remarks>
/// Resolution is synchronous and happens the first time a reference to an unknown resource is
/// followed, during loading or evaluation. The library performs no network I/O itself; a resolver that
/// fetches documents decides how. A resolver may be called concurrently and must be thread-safe.
/// </remarks>
public interface IJsonSchemaResolver
{
    /// <summary>
    /// Attempts to supply the schema document identified by a URI.
    /// </summary>
    /// <param name="uri">The absolute URI of the document, without a fragment.</param>
    /// <param name="schema">The document's root value when it is found.</param>
    /// <returns><see langword="true"/> when the document was found.</returns>
    bool TryResolve(Uri uri, out JsonElement schema);
}
