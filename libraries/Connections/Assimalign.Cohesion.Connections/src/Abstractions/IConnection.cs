using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents a live, bidirectional byte channel between two endpoints.
/// </summary>
/// <remarks>
/// <para>
/// A connection is the unit of byte transfer in the Cohesion networking stack, and it
/// <em>is</em> a duplex pipe: <see cref="IDuplexPipe.Input"/> yields the bytes received from the
/// remote peer, and <see cref="IDuplexPipe.Output"/> accepts the bytes to send to it. Both names
/// are anchored to the holder of the connection; the mirrored pair a transport uses internally to
/// pump the wire is an implementation detail and never appears on the contract.
/// </para>
/// <para>
/// For a stream transport (such as TCP) a connection maps to a single socket; for a multiplexed
/// transport (such as QUIC) each accepted or opened stream is itself an <see cref="IConnection"/>.
/// A connection is already live when handed to a consumer — read and write immediately. Complete
/// <see cref="IDuplexPipe.Output"/> to signal a graceful half-close, dispose the connection to
/// close it, or call <see cref="Abort(Exception)"/> to tear it down immediately.
/// </para>
/// </remarks>
public interface IConnection : IDuplexPipe, IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this connection.
    /// </summary>
    ConnectionId Id { get; }

    /// <summary>
    /// Gets the local endpoint the connection is bound to, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Gets the remote endpoint the connection is connected to, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets which halves of the connection's duplex pipe are usable.
    /// </summary>
    /// <remarks>
    /// Stream transports always report <see cref="ConnectionDirection.Bidirectional"/>; unidirectional
    /// streams of a multiplexed transport report <see cref="ConnectionDirection.ReadOnly"/> or
    /// <see cref="ConnectionDirection.WriteOnly"/>.
    /// </remarks>
    ConnectionDirection Direction { get; }

    /// <summary>
    /// Gets the capabilities (delivery guarantees, multiplexing, and security) of the underlying transport.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the current lifecycle state of the connection.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Gets a token that is signaled when the connection is closed or aborted.
    /// </summary>
    CancellationToken ConnectionClosed { get; }

    /// <summary>
    /// Aborts the connection immediately, discarding any in-flight data.
    /// </summary>
    /// <param name="reason">An optional exception describing why the connection was aborted.</param>
    void Abort(Exception? reason = null);
}
