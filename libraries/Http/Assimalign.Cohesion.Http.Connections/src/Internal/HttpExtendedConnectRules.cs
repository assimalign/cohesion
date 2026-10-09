namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The extended CONNECT (RFC 8441, RFC 9220) accept rules the HTTP/2 and HTTP/3 exchange controls
/// share (<see cref="IHttpExchangeControl.AcceptTunnelAsync"/>): the refusal messages, and the
/// <c>200</c> head a tunnel answers with.
/// </summary>
/// <remarks>
/// <para>
/// Both controls run the same guards in the same order, each before anything is written: the
/// exchange must be an extended CONNECT; the tunnel is accepted at most once (the attempt latches
/// even when a later guard refuses it); the exchange must not be cancelled; its response must not
/// have started (or, on HTTP/2, been claimed); its stream must not be reset nor its connection
/// closed. Only then is the head prepared, the stream's final response claimed (HTTP/2) and marked
/// started, the tunnel registered on the exchange, the head written, and the head marked committed.
/// </para>
/// <para>
/// Registering the tunnel before the head is written is what takes the exchange over: from then on
/// the exchange reports <see cref="HttpExchangeDirective.TakeOver"/>, so neither the buffered send
/// path nor the raw response body sink can put a second head on the stream, even if writing the
/// <c>200</c> fails. The response-head and after-response interceptor hooks do not run for a tunnel,
/// as for an HTTP/1.1 takeover, so a WebSocket behaves the same on all three versions.
/// </para>
/// </remarks>
internal static class HttpExtendedConnectRules
{
    /// <summary>The refusal for an exchange that is not an extended CONNECT.</summary>
    public const string NotExtendedConnectMessage =
        "The exchange is not an extended CONNECT (RFC 8441, RFC 9220), so no tunnel can be accepted for it.";

    /// <summary>The refusal for a second accept.</summary>
    public const string AlreadyAcceptedMessage =
        "The extended CONNECT tunnel has already been accepted for this exchange.";

    /// <summary>The refusal for a cancelled exchange.</summary>
    public const string CancelledMessage =
        "The extended CONNECT tunnel cannot be accepted: the exchange was cancelled.";

    /// <summary>The refusal for an exchange whose response has started.</summary>
    public const string ResponseStartedMessage =
        "The extended CONNECT tunnel cannot be accepted after the response has started.";

    /// <summary>
    /// Prepares the response head that establishes the tunnel: status <c>200</c>, and no
    /// <c>Content-Length</c>.
    /// </summary>
    /// <param name="response">The exchange's response, whose headers the application may have set.</param>
    public static void PrepareResponseHead(HttpResponse response)
    {
        // RFC 8441 §5 / RFC 9220 §3 — a 2xx response establishes the tunnel.
        response.StatusCode = HttpStatusCode.Ok;

        HttpHeaderCollection headers = response.Headers;

        // RFC 9110 §9.3.6 — a 2xx response to CONNECT carries no Content-Length or Transfer-Encoding: the
        // stream that follows is the tunnel, not a body. Transfer-Encoding and the other
        // connection-specific fields, such as the HTTP/1.1 upgrade fields an application might set, never
        // reach an HTTP/2 or HTTP/3 head: the encoders drop them from every response head
        // (HttpResponseFieldRules, RFC 9113 §8.2.2, RFC 9114 §4.2).
        headers.Remove(HttpHeaderKey.ContentLength);
    }
}
