using System;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents the identity, capabilities, features, and lifetime of an established connection.
/// </summary>
/// <remarks>
/// The code that accepts or establishes a connection owns its disposal and is the only code
/// that reads or writes it. Inspecting a listener's connection collection does not transfer ownership.
/// </remarks>
public interface IConnection : IAsyncDisposable
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
    /// Gets the capabilities (delivery guarantees, multiplexing, and security) of the underlying transport.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the current lifecycle state of the connection.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Gets the optional capabilities and facts registered for this connection.
    /// </summary>
    /// <remarks>
    /// A layer exposes its wrapped connection's features unless it replaces or removes their names.
    /// A multiplexed stream owns its features independently of its parent connection.
    /// </remarks>
    IConnectionFeatureCollection Features { get; }

    /// <summary>
    /// Gets a token signaled on close, abort, or peer closure, no later than disposal completing.
    /// </summary>
    /// <remarks>
    /// A clean half-close of a multiplexed stream does not end its lifetime. A peer reset or
    /// abandonment does, even when the owner is not reading or writing the stream.
    /// </remarks>
    CancellationToken ConnectionClosed { get; }

    /// <summary>
    /// Ends the connection immediately, discarding in-flight data and signaling <see cref="ConnectionClosed"/>.
    /// </summary>
    /// <param name="reason">An optional exception describing why the connection was aborted.</param>
    void Abort(Exception? reason = null);
}
