using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Security.Tests;

/// <summary>
/// An in-memory <see cref="Connection"/> double over a supplied reader/writer pair, used by
/// <see cref="InMemoryConnectionPair"/> to build two cross-wired connections. Tracks aborts and
/// disposal so decorator behavior is observable.
/// </summary>
internal sealed class TestPipeConnection : Connection
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionCapabilities _capabilities;
    private readonly ConnectionId _id = ConnectionId.New();
    private readonly EndPoint _localEndPoint;
    private readonly EndPoint _remoteEndPoint;
    private ConnectionState _state = ConnectionState.Open;

    public TestPipeConnection(
        PipeReader input,
        PipeWriter output,
        EndPoint localEndPoint,
        EndPoint remoteEndPoint,
        ConnectionCapabilities? capabilities = null)
    {
        _input = input;
        _output = output;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        _capabilities = capabilities ?? DefaultCapabilities;
    }

    public static ConnectionCapabilities DefaultCapabilities { get; } = new(
        ConnectionProtocol.Memory,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        Security: ConnectionSecurity.None);

    public bool IsAborted { get; private set; }

    public bool IsDisposed { get; private set; }

    public Exception? AbortReason { get; private set; }

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint => _localEndPoint;

    public override EndPoint? RemoteEndPoint => _remoteEndPoint;

    public override PipeReader Input => _input;

    public override PipeWriter Output => _output;

    public override ConnectionCapabilities Capabilities => _capabilities;

    public override ConnectionState State => _state;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    public override void Abort(Exception? reason = null)
    {
        IsAborted = true;
        AbortReason = reason;
        _state = ConnectionState.Aborted;
        _closedSource.Cancel();
    }

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _state = ConnectionState.Closed;
        _output.Complete();
        _input.Complete();
        _closedSource.Cancel();
        return ValueTask.CompletedTask;
    }
}
