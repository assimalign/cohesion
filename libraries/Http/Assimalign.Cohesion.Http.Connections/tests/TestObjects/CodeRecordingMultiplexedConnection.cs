using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// The server end of a multiplexed connection that records how the HTTP/3 engine closed it. It carries
/// a close code through the root, so the engine hands it the RFC 9114 §8.1 code it would put on the
/// wire. Everything else passes through to the wrapped connection.
/// </summary>
internal sealed class CodeRecordingMultiplexedConnection : MultiplexedConnection
{
    private readonly MultiplexedConnection _inner;

    public CodeRecordingMultiplexedConnection(MultiplexedConnection inner)
    {
        _inner = inner;
    }

    /// <summary>Gets the code the connection was closed with, or <see langword="null"/> when it was not aborted with one.</summary>
    public long? AbortErrorCode { get; private set; }

    /// <summary>Gets the reason passed to the most recent abort, or <see langword="null"/>.</summary>
    public Exception? AbortReason { get; private set; }

    /// <inheritdoc />
    protected override IConnectionFeatureCollection InnerFeatures => _inner.Features;

    /// <inheritdoc />
    public override ConnectionId Id => _inner.Id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public override ConnectionState State => _inner.State;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    /// <inheritdoc />
    public override ValueTask<MultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
        => _inner.AcceptStreamAsync(cancellationToken);

    /// <inheritdoc />
    public override ValueTask<MultiplexedStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
        => _inner.OpenStreamAsync(direction, cancellationToken);

    /// <inheritdoc />
    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        AbortErrorCode = errorCode;
        AbortReason = reason;
        _inner.Abort(errorCode, reason);
    }

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        AbortReason = reason;
        _inner.Abort(reason);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
