namespace Assimalign.Cohesion.Http;

/// <summary>
/// The control-flow state of an HTTP exchange as directed through
/// <see cref="IHttpExchangeControl"/> — how the connection (or connection context) handling the
/// current exchange should proceed.
/// </summary>
/// <remarks>
/// The transport reads this state at its lifecycle checkpoints (before committing the final
/// response head, before reusing the connection) and acts on it with the wire behavior
/// appropriate to the protocol version. Interceptor hooks and feature packages drive transitions
/// through <see cref="IHttpExchangeControl.Abort"/> and <see cref="IHttpExchangeControl.TakeOver"/>;
/// the directive never transitions back to <see cref="Continue"/>.
/// </remarks>
public enum HttpExchangeDirective
{
    /// <summary>
    /// The transport continues driving the exchange normally: the application's final response is
    /// written and, where the protocol allows, the connection is reused.
    /// </summary>
    Continue = 0,

    /// <summary>
    /// The exchange is aborted at the protocol layer. The transport rejects it with the wire
    /// behavior appropriate to the version — HTTP/1.1 ends the exchange and the connection,
    /// HTTP/2 resets the stream (<c>RST_STREAM</c>), HTTP/3 resets the request stream — while a
    /// multiplexed connection stays alive for its other streams.
    /// </summary>
    Abort = 1,

    /// <summary>
    /// The transport has given up control of the exchange: a feature took over the underlying
    /// byte stream (<see cref="IHttpExchangeControl.TakeOver"/>), so the transport suppresses its
    /// own response for the exchange and stops reusing the connection for further HTTP requests.
    /// </summary>
    TakeOver = 2,
}
