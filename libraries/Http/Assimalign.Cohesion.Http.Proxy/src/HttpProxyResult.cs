namespace Assimalign.Cohesion.Http;

/// <summary>
/// Describes how a forwarded exchange or a tunnel ended.
/// </summary>
/// <remarks>
/// The proxy has already answered the client by the time a result is returned: each failure that
/// happens before the response starts sets the status code named on its value, and a failure after
/// the response started aborts the exchange. A caller reads the result to log or count outcomes,
/// never to write a second response.
/// </remarks>
public enum HttpProxyResult
{
    /// <summary>
    /// The destination's response, upgraded stream or tunnel was relayed to the end.
    /// </summary>
    Completed = 0,

    /// <summary>
    /// The proxy answered the request itself, as the protocol requires: an <c>OPTIONS</c> or
    /// <c>TRACE</c> request whose <c>Max-Forwards</c> reached zero (200 for <c>OPTIONS</c>, 501 for
    /// <c>TRACE</c>).
    /// </summary>
    AnsweredByProxy = 1,

    /// <summary>
    /// The caller's token was canceled, the client went away, or the client stopped receiving. The
    /// exchange is aborted, so the client receives nothing more.
    /// </summary>
    Canceled = 2,

    /// <summary>
    /// The destination could not be reached: name resolution, the connection or the TLS handshake
    /// failed. Answered with 502 Bad Gateway.
    /// </summary>
    DestinationUnreachable = 3,

    /// <summary>
    /// The destination stopped making progress for longer than the activity timeout before its
    /// response head arrived, or a tunnel's connection attempt timed out. Answered with 504 Gateway
    /// Timeout.
    /// </summary>
    DestinationTimedOut = 4,

    /// <summary>
    /// The destination failed before its response head arrived, for a reason other than the ones
    /// above, such as a malformed response or a reset. Answered with 502 Bad Gateway.
    /// </summary>
    DestinationFailed = 5,

    /// <summary>
    /// Reading the client's request body failed. Answered with 400 Bad Request, which the transport
    /// replaces with the status of the failure it detected (such as 413) when it has one.
    /// </summary>
    RequestBodyFailed = 6,

    /// <summary>
    /// The response body could not be relayed after the destination's response head arrived: the
    /// destination's body failed or timed out. The exchange is aborted when its response has started,
    /// and answered with 502 Bad Gateway when it has not.
    /// </summary>
    ResponseBodyFailed = 7,

    /// <summary>
    /// The response had to be buffered and its body was larger than
    /// <see cref="HttpForwarderOptions.MaxBufferedResponseBodySize"/>. Answered with 502 Bad Gateway.
    /// </summary>
    ResponseTooLarge = 8,

    /// <summary>
    /// The destination accepted a protocol upgrade the proxy could not verify: it switched to another
    /// protocol than the one requested, returned a WebSocket accept value that does not match its key,
    /// or answered an extended <c>CONNECT</c> with a success status other than 101. Answered with 502
    /// Bad Gateway.
    /// </summary>
    UpgradeFailed = 9,

    /// <summary>
    /// The request needs something this proxy does not do: a <c>CONNECT</c> request reached the
    /// forwarder instead of a tunnel, an extended <c>CONNECT</c> named a protocol other than
    /// <c>websocket</c>, or a tunnel was asked to relay a request that is not an HTTP/1.1
    /// <c>CONNECT</c>. Answered with 501 Not Implemented.
    /// </summary>
    NotSupported = 10,

    /// <summary>
    /// The request's <c>Via</c> header already names this forwarder's pseudonym, so forwarding it
    /// would loop. Answered with 508 Loop Detected.
    /// </summary>
    LoopDetected = 11,
}
