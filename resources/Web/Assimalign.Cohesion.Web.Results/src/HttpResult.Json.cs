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
/// A buffered JSON result for an open (caller-defined) DTO. The value is serialized through the
/// source-generated <see cref="JsonTypeInfo{T}"/> the endpoint author supplies — never through
/// reflection — so the result is NativeAOT- and trimming-safe by construction.
/// </summary>
/// <typeparam name="T">The DTO type being serialized.</typeparam>
/// <remarks>
/// <para>
/// Created through <see cref="Results.Json{T}"/> or <see cref="TypedResults.Json{T}"/>; the
/// constructor is internal so the factories remain the only entry point. The carrier is immutable
/// and may be reused across exchanges.
/// </para>
/// <para>
/// This is the <c>JsonTypeInfo&lt;T&gt;</c> half of the serialization rule recorded in the
/// project's <c>docs/DESIGN.md</c>: open DTOs serialize through caller-supplied source-gen
/// metadata, while fixed framework payloads (<see cref="ProblemDetails"/>) use a hand-rolled
/// <see cref="Utf8JsonWriter"/> writer.
/// </para>
/// </remarks>
public sealed class JsonHttpResult<T> : IResult
{
    internal JsonHttpResult(T? value, JsonTypeInfo<T> typeInfo, string? contentType, HttpStatusCode? statusCode)
    {
        Value = value;
        TypeInfo = typeInfo;
        ContentType = contentType ?? HttpResultDefaults.JsonMediaType;
        StatusCode = statusCode;
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
    /// Gets the <c>Content-Type</c> this result sets. Defaults to
    /// <c>application/json; charset=utf-8</c> when the factory was given none.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the status code this result sets, or <see langword="null"/> to leave the response's
    /// current status untouched.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Serializes <see cref="Value"/> via <see cref="JsonSerializer.Serialize{TValue}(Utf8JsonWriter, TValue, JsonTypeInfo{TValue})"/>
    /// and writes the payload with <see cref="ContentType"/>, <c>Content-Length</c>, and (when set)
    /// <see cref="StatusCode"/>.
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

        return HttpResultWriter.WritePayloadAsync(context, StatusCode, ContentType, buffer.WrittenMemory, cancellationToken);
    }
}
