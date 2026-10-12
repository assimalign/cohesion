using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.StreamLimits.Tests.TestObjects;

internal sealed class TestConnection : MultiplexedConnection
{
    private readonly CancellationTokenSource _closedSource = new();
    private ConnectionState _state = ConnectionState.Open;

    public override ConnectionId Id { get; } = ConnectionId.New();
    public override EndPoint? LocalEndPoint => null;
    public override EndPoint? RemoteEndPoint => null;
    public override ConnectionCapabilities Capabilities => default;
    public override ConnectionState State => _state;
    public override CancellationToken ConnectionClosed => _closedSource.Token;
    public override ValueTask<MultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override ValueTask<MultiplexedStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override void Abort(Exception? reason = null)
    {
        _state = ConnectionState.Aborted;
        _closedSource.Cancel();
    }
    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        Abort(reason);
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
