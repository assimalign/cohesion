using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default content serializer: UTF-8 JSON through source-generated metadata.
/// </summary>
/// <remarks>
/// <para>
/// The serializer looks each type up in this library's own source-generated context first, then in
/// the contexts it was created with, in order, and writes the type with the options of the context
/// that declares it. An area's types therefore keep the naming its own context chose, and this
/// library's types keep camel case whatever the other contexts choose. Contexts are internal to the
/// assembly that declares them: a platform that exchanges JSON declares its own context for the
/// public types it needs, such as an area's state types.
/// </para>
/// <para>
/// Both halves of a platform can write their content with it: an instance's control plane writes its
/// state and results, and a platform that carries a manifest, a publication, or a result between
/// processes writes them as this library's own context does.
/// </para>
/// <para>
/// It discovers nothing by reflection: a type no context declares is not supported, so a resource's
/// serializer is created with the contexts of every action's input, output, and state types.
/// </para>
/// </remarks>
public sealed class JsonApplicationContentSerializer : IApplicationContentSerializer
{
    private readonly JsonSerializerContext[] _contexts;

    private JsonApplicationContentSerializer(JsonSerializerContext[] contexts)
    {
        _contexts = contexts;
    }

    /// <summary>
    /// Gets the media type of the content, <c>application/json</c>.
    /// </summary>
    public string MediaType => "application/json";

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<T>());
    }

    /// <inheritdoc/>
    public T? Deserialize<T>(ReadOnlyMemory<byte> content)
    {
        JsonTypeInfo<T> typeInfo = GetTypeInfo<T>();
        try
        {
            return JsonSerializer.Deserialize(content.Span, typeInfo);
        }
        catch (JsonException exception)
        {
            throw new ApplicationContentException(exception.Message, exception);
        }
    }

    /// <summary>
    /// Creates a JSON serializer that knows this library's types and the types the specified
    /// contexts declare.
    /// </summary>
    /// <param name="contexts">
    /// The source-generated contexts of the resource's action input, output, and state types, such as
    /// an area's resource context. Earlier contexts win when two declare one type.
    /// </param>
    /// <returns>The serializer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contexts"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="contexts"/> contains <see langword="null"/>.</exception>
    public static JsonApplicationContentSerializer Create(params JsonSerializerContext[] contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        foreach (JsonSerializerContext context in contexts)
        {
            if (context is null)
            {
                throw new ArgumentException(ErrorMessages.JsonContextNull, nameof(contexts));
            }
        }

        return new JsonApplicationContentSerializer([ApplicationProtocolJsonContext.Default, .. contexts]);
    }

    private JsonTypeInfo<T> GetTypeInfo<T>()
    {
        foreach (JsonSerializerContext context in _contexts)
        {
            if (context.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> typeInfo)
            {
                return typeInfo;
            }
        }

        throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationContentTypeUnsupported, typeof(T)));
    }
}
