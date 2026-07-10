using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// A <c>200 OK</c> result carrying a caller-defined DTO, serialized as JSON through the
/// source-generated <see cref="JsonTypeInfo{T}"/> the endpoint author supplies.
/// </summary>
/// <typeparam name="T">The DTO type being returned.</typeparam>
/// <remarks>
/// <para>
/// Created through <see cref="Results.Ok{T}"/> or <see cref="TypedResults.Ok{T}"/>; the constructor
/// is internal so the factories remain the only entry point. The carrier is immutable and may be
/// reused across exchanges.
/// </para>
/// <para>
/// <b>JSON-only, by design.</b> In this foundation <c>Ok&lt;T&gt;</c> always renders
/// <c>application/json</c> — it does not inspect the request's <c>Accept</c> header. Content
/// negotiation is a deliberate seam deferred to the negotiated-results slice (#149), which layers an
/// <c>IResultFormatter</c> registry over the <c>HttpMediaType</c>/<c>Accept</c> primitives; when
/// that lands, negotiation-aware behavior arrives as a new composition over this carrier, not a
/// breaking change to it. See the project's <c>docs/DESIGN.md</c>.
/// </para>
/// </remarks>
public sealed class OkHttpResult<T> : IResult
{
    internal OkHttpResult(T? value, JsonTypeInfo<T> typeInfo)
    {
        Value = value;
        TypeInfo = typeInfo;
    }

    /// <summary>
    /// Gets the value serialized as the response body. A <see langword="null"/> reference
    /// serializes as JSON <c>null</c>.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Gets the source-generated serialization metadata used to write <see cref="Value"/>.
    /// </summary>
    public JsonTypeInfo<T> TypeInfo { get; }

    /// <summary>
    /// Gets the status code this result sets on the response: <c>200 OK</c>.
    /// </summary>
    public HttpStatusCode StatusCode => HttpStatusCode.Ok;

    /// <summary>
    /// Sets <c>200 OK</c>, serializes <see cref="Value"/> via
    /// <see cref="JsonSerializer.Serialize{TValue}(Utf8JsonWriter, TValue, JsonTypeInfo{TValue})"/>,
    /// and writes the payload as <c>application/json; charset=utf-8</c> with <c>Content-Length</c>.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the body write.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            // A null reference is legal here: the serializer emits JSON null for it, so the
            // null-forgiving cast never produces an invalid write.
            JsonSerializer.Serialize(writer, Value!, TypeInfo);
        }

        return HttpResultWriter.WritePayloadAsync(
            context,
            HttpStatusCode.Ok,
            HttpResultDefaults.JsonMediaType,
            buffer.WrittenMemory,
            cancellationToken);
    }
}
