using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IConnection"/>.
/// </summary>
public abstract class Connection : IConnection
{
    /// <inheritdoc />
    public abstract ConnectionId Id { get; }

    /// <inheritdoc />
    public abstract EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public abstract EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the reader for the bytes received from the remote peer.
    /// </summary>
    public abstract PipeReader Input { get; }

    /// <summary>
    /// Gets the writer for the bytes to send to the remote peer.
    /// </summary>
    public abstract PipeWriter Output { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Defaults to <see cref="ConnectionDirection.Bidirectional"/>; unidirectional streams of a
    /// multiplexed transport override this.
    /// </remarks>
    public virtual ConnectionDirection Direction => ConnectionDirection.Bidirectional;

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public abstract ConnectionState State { get; }

    /// <inheritdoc />
    public abstract CancellationToken ConnectionClosed { get; }

    /// <inheritdoc />
    public abstract void Abort(Exception? reason = null);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();
}
