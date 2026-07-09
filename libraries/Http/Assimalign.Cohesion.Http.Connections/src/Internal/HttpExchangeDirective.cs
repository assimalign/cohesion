namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The transport-internal control-flow state of an exchange — the unified view each connection
/// context consults at its lifecycle checkpoints (before committing the final response head,
/// before reusing the connection). Derived from the exchange's flags rather than stored:
/// an application-driven <see cref="IHttpContext.Cancel"/> maps to <see cref="Abort"/>, and an
/// HTTP/1.1 takeover (<see cref="IHttpExchangeControl.TakeOver"/>, which finalizes the exchange
/// out-of-band) maps to <see cref="TakeOver"/>. Deliberately internal: aborting is authored on
/// the application surface (<see cref="IHttpContext.Cancel"/>) and takeover on the exchange
/// control — this enum is how the transport reads the result, not a public seam.
/// </summary>
internal enum HttpExchangeDirective
{
    /// <summary>
    /// The transport continues driving the exchange normally: the application's final response is
    /// written and, where the protocol allows, the connection is reused.
    /// </summary>
    Continue = 0,

    /// <summary>
    /// The exchange was aborted at the protocol layer. The transport rejects it with the wire
    /// behavior appropriate to the version — HTTP/1.1 writes no response and ends the connection
    /// after the exchange, HTTP/2 resets the stream (<c>RST_STREAM</c>), HTTP/3 resets the
    /// request stream — while a multiplexed connection stays alive for its other streams.
    /// </summary>
    Abort = 1,

    /// <summary>
    /// The transport has given up control of the exchange: a feature took over the underlying
    /// byte stream, so the transport suppresses its own response for the exchange and stops
    /// reusing the connection for further HTTP requests.
    /// </summary>
    TakeOver = 2,
}
