using System;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// The fault thrown by the content-serialization registry when typed body IO cannot proceed:
/// no registry is composed on the application, no reader/writer is registered for the exchange's
/// media type, or the resolved serializer carries no contract for the requested CLR type.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>fault</em> in the area's faults-vs-outcomes model — it signals a composition gap
/// or an unnegotiated call, and flows to the application's <c>OnError</c> hook like any other
/// exception. Layers that want to produce protocol <em>outcomes</em> instead (a <c>415</c> for an
/// unsupported <c>Content-Type</c>, a <c>406</c> for a failed negotiation) consult the
/// non-throwing <see cref="IHttpContentSerializationFeature"/> lookup surface first and never see
/// this exception.
/// </para>
/// <para>
/// Malformed payloads are not wrapped: format-native exceptions (e.g.
/// <see cref="System.Text.Json.JsonException"/>) propagate as-is so callers can branch on them.
/// </para>
/// </remarks>
public sealed class HttpContentSerializationException : Exception
{
    /// <summary>
    /// Initializes the exception with a message describing the serialization fault.
    /// </summary>
    /// <param name="message">The message describing the fault.</param>
    public HttpContentSerializationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes the exception with a message and the underlying cause.
    /// </summary>
    /// <param name="message">The message describing the fault.</param>
    /// <param name="innerException">The underlying cause.</param>
    public HttpContentSerializationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
