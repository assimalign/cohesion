using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// Shared pieces of the built-in JSON reader/writer pair: the media types the pair claims and
/// the contract-metadata lookup that turns a missing source-generated contract into the
/// registry's fault.
/// </summary>
internal static class JsonContentDefaults
{
    /// <summary>The <c>text/json</c> legacy alias, claimed alongside <see cref="HttpMediaType.ApplicationJson"/>.</summary>
    internal static HttpMediaType TextJson { get; } = HttpMediaType.Parse("text/json");

    /// <summary>
    /// The media types the JSON pair claims. <c>application/json</c> first — it is the writer's
    /// canonical content type.
    /// </summary>
    internal static HttpMediaType[] MediaTypes { get; } = [HttpMediaType.ApplicationJson, TextJson];

    /// <summary>
    /// Resolves the serialization contract for <paramref name="type"/> from the registered
    /// resolver, throwing the registry's fault when the resolver carries none — under NativeAOT
    /// there is no reflection fallback, so an unregistered type is a composition error.
    /// </summary>
    internal static JsonTypeInfo GetRequiredTypeInfo(JsonSerializerOptions options, Type type)
    {
        if (!options.TryGetTypeInfo(type, out JsonTypeInfo? typeInfo))
        {
            throw new HttpContentSerializationException(
                $"The registered JSON type-info resolver has no serialization contract for '{type}'. " +
                "Add the type to the application's JsonSerializerContext (e.g. [JsonSerializable(typeof(" + type.Name + "))]) " +
                "or register a resolver that covers it via AddJsonSerialization.");
        }

        return typeInfo;
    }
}
