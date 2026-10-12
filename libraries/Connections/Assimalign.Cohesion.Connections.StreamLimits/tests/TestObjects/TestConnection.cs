using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.StreamLimits.Tests.TestObjects;

internal sealed class TestConnection : MultiplexedConnection
{
    public override ConnectionId Id { get; } = ConnectionId.New();
    public override EndPoint? LocalEndPoint => null;
    public override EndPoint? RemoteEndPoint => null;
    public override ConnectionCapabilities Capabilities => default;
    public override ConnectionState State => ConnectionState.Open;
    public override CancellationToken ConnectionClosed => CancellationToken.None;
    public override ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override void Abort(Exception? reason = null) { }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
