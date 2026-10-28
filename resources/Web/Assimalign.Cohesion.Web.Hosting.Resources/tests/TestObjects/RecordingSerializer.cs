using System;
using System.Collections.Concurrent;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>
/// A platform's own content serializer: JSON under its own media type, recording every type it
/// writes, so a test can see which content went through it.
/// </summary>
internal sealed class RecordingSerializer : IResourceContentSerializer
{
    private readonly JsonResourceContentSerializer _json = JsonResourceContentSerializer.Create(WebResourceJsonContext.Default);

    public ConcurrentQueue<Type> Written { get; } = new();

    public string MediaType => "application/vnd.cohesion.test+json";

    public ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        Written.Enqueue(typeof(T));
        return _json.Serialize(value);
    }

    public T? Deserialize<T>(ReadOnlyMemory<byte> content)
    {
        return _json.Deserialize<T>(content);
    }
}
