using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// A <see cref="Connection"/> decorator used by <see cref="RecordingConnectionLayer"/> so tests
/// can observe which layer produced a connection and unwrap back to the inner connection.
/// </summary>
internal sealed class LayerWrappedConnection : Connection
{
    public LayerWrappedConnection(IConnection inner, string layerName)
    {
        Inner = inner;
        LayerName = layerName;
    }

    public IConnection Inner { get; }

    public string LayerName { get; }

    public override ConnectionId Id => Inner.Id;

    public override EndPoint? LocalEndPoint => Inner.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => Inner.RemoteEndPoint;

    public override PipeReader Input => Inner.Input;

    public override PipeWriter Output => Inner.Output;

    public override ConnectionDirection Direction => Inner.Direction;

    public override ConnectionCapabilities Capabilities => Inner.Capabilities;

    public override ConnectionState State => Inner.State;

    public override CancellationToken ConnectionClosed => Inner.ConnectionClosed;

    public override void Abort(Exception? reason = null) => Inner.Abort(reason);

    public override ValueTask DisposeAsync() => Inner.DisposeAsync();
}
