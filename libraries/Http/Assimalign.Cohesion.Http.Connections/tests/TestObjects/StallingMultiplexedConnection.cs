using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// The server end of a multiplexed connection whose accepted request streams (the bidirectional streams
/// the client opens) are <see cref="StallingConnection"/>s, so a test controls how fast each response is
/// accepted — the QUIC stream credit (<c>MAX_STREAM_DATA</c>) an HTTP/3 client grants. Unidirectional
/// streams and the streams the server opens pass through.
/// </summary>
internal sealed class StallingMultiplexedConnection : MultiplexedConnection
{
    private readonly MultiplexedConnection _inner;
    private readonly List<StallingConnection> _requestStreams = new();

    public StallingMultiplexedConnection(MultiplexedConnection inner)
    {
        _inner = inner;
    }

    /// <summary>Gets the request streams the server accepted, in order.</summary>
    public IReadOnlyList<StallingConnection> RequestStreams
    {
        get
        {
            lock (_requestStreams)
            {
                return _requestStreams.ToArray();
            }
        }
    }

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
    public override async ValueTask<MultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        MultiplexedStream stream = await _inner.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);

        if (stream.Direction != ConnectionDirection.Bidirectional)
        {
            return stream;
        }

        StallingConnection stalling = new(stream);

        lock (_requestStreams)
        {
            _requestStreams.Add(stalling);
        }

        return stalling;
    }

    /// <inheritdoc />
    public override ValueTask<MultiplexedStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
        => _inner.OpenStreamAsync(direction, cancellationToken);

    /// <inheritdoc />
    public override void Abort(long errorCode, Exception? reason = null) => _inner.Abort(errorCode, reason);

    public override void Abort(Exception? reason = null) => _inner.Abort(reason);

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
