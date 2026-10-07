using System;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Transport-state queries over the exchanges an <see cref="IHttpConnectionContext"/> yields from
/// <see cref="IHttpConnectionContext.ReceiveAsync"/>, for the host that drives the connection.
/// </summary>
/// <remarks>
/// The state these members report lives on the transport's own exchange types, so they describe the
/// exchanges produced by the connection contexts in this package and report the neutral answer for
/// any other <see cref="IHttpContext"/> implementation.
/// </remarks>
public static class HttpContextTransportExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets a value indicating whether the exchange's final response has started: its status line
        /// and header block have been, or are being, committed to the wire, so the response can no
        /// longer be replaced.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The final response starts at the first write or flush through the raw response body sink
        /// that response interceptors expose (the incremental-streaming path, for example
        /// <c>Assimalign.Cohesion.Http.Streaming</c>), when an HTTP/2 or HTTP/3 extended CONNECT
        /// tunnel is accepted (<see cref="IHttpExtendedConnectFeature.AcceptAsync"/>), or at the
        /// commit point of <see cref="IHttpConnectionContext.SendAsync"/>. An interim (<c>1xx</c>)
        /// response does not start it. This is the state
        /// <see cref="IHttpExchangeControl.HasResponseStarted"/> reports to response interceptors,
        /// exposed to the host.
        /// </para>
        /// <para>
        /// A host that finalizes an exchange whose application processing failed reads it to choose
        /// an honest outcome. Before the start the host can still replace the response, for example
        /// with <c>500 Internal Server Error</c>, and send it. After the start the replacement cannot
        /// reach the wire, and sending would finalize the started response as if it were complete;
        /// the host calls <see cref="IHttpContext.Cancel"/> and then
        /// <see cref="IHttpConnectionContext.SendAsync"/> instead, which resets the exchange (an
        /// HTTP/2 <c>RST_STREAM</c>, an HTTP/3 stream abort, or an HTTP/1.1 connection that ends after
        /// the exchange).
        /// </para>
        /// <para>
        /// Returns <see langword="false"/> for an <see cref="IHttpContext"/> that was not produced by
        /// this package's connection contexts, because the transport cannot observe its response.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when the exchange is <see langword="null"/>.</exception>
        public bool HasResponseStarted
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                return context is TransportHttpContext transport && transport.HasFinalResponseStarted;
            }
        }
    }
}
