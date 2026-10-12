using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.StreamAbort.Tests.TestObjects;

internal sealed class TestConnection : Connection
{
    public override ConnectionId Id { get; } = ConnectionId.New();
    public override EndPoint? LocalEndPoint => null;
    public override EndPoint? RemoteEndPoint => null;
    public override ConnectionCapabilities Capabilities => default;
    public override ConnectionState State => ConnectionState.Open;
    public override CancellationToken ConnectionClosed => CancellationToken.None;
    public override PipeReader Input => throw new NotSupportedException();
    public override PipeWriter Output => throw new NotSupportedException();
    public override void Abort(Exception? reason = null) { }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
