using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Transports;

public interface ITransportConnectionContext
{
    /// <summary>
    /// The local endpoint the connection is bound to.
    /// </summary>
    EndPoint LocalEndPoint { get; }

    /// <summary>
    /// The remote endpoint the connection is bound to.
    /// </summary>
    EndPoint RemoteEndPoint { get; }

    /// <summary>
    /// A pipe to send and receive data from either client or server.
    /// </summary>
    ITransportConnectionPipe Pipe { get; }

    /// <summary>
    /// Gets a cancellation token that will be triggered when the connection is closed. This acts as a lifetime for the
    /// connection and can be used to trigger cleanup of resources associated with the connection.
    /// </summary>
    CancellationToken ConnectionCancelled { get; }

    /// <summary>
    /// Gets a value indicating whether the stream this context represents is
    /// bidirectional. Single-stream transports (TCP) and ordinary multiplex
    /// request streams are bidirectional; a multiplex transport (QUIC) can also
    /// surface unidirectional streams — for example the HTTP/3 control and
    /// QPACK streams — which report <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Defined as a default interface member returning <see langword="true"/>
    /// so existing context implementations are unaffected; only multiplex
    /// transports that expose unidirectional streams need to override it.
    /// </remarks>
    bool IsBidirectional => true;

    /// <summary>
    ///
    /// </summary>
    IDictionary<string, object?> Items { get; }
}