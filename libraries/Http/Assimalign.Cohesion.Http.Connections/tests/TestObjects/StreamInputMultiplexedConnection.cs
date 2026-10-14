using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// The server end of a multiplexed connection whose accepted request streams (the bidirectional streams the
/// client opens) are <see cref="StreamInputConnection"/>s, so the HTTP/3 layer reads them the way it reads
/// the QUIC driver's: through a <see cref="System.IO.Pipelines.PipeReader"/> over a stream, which sees the
/// peer's FIN only on a read that returns nothing, with a completion member exposing an end the transport
/// already knows. Unidirectional streams and the streams the server opens pass through.
/// </summary>
internal sealed class StreamInputMultiplexedConnection : MultiplexedConnection
{
    private readonly MultiplexedConnection _inner;

    public StreamInputMultiplexedConnection(MultiplexedConnection inner)
    {
        _inner = inner;
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
    public override async ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        MultiplexedConnectionStream stream = await _inner.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);

        return stream.Direction == ConnectionDirection.Bidirectional ? new StreamInputConnection(stream) : stream;
    }

    /// <inheritdoc />
    public override ValueTask<MultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
        => _inner.OpenStreamAsync(direction, cancellationToken);

    /// <inheritdoc />
    public override void Abort(long errorCode, Exception? reason = null) => _inner.Abort(errorCode, reason);

    public override void Abort(Exception? reason = null) => _inner.Abort(reason);

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
