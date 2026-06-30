using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// An in-memory <see cref="Connection"/> double built over two <see cref="Pipe"/> instances.
/// The far ends are exposed so a test can play the remote peer: bytes written to
/// <see cref="PeerOutput"/> appear on the connection's <see cref="Input"/>, and bytes the
/// connection writes to <see cref="Output"/> can be observed on <see cref="PeerInput"/>.
/// </summary>
internal sealed class TestConnection : Connection
{
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionDirection _direction;
    private readonly ConnectionCapabilities _capabilities;
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;

    public TestConnection(
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        ConnectionCapabilities? capabilities = null)
    {
        _direction = direction;
        _capabilities = capabilities ?? DefaultCapabilities;
        _receivePipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        _sendPipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
    }

    public static ConnectionCapabilities DefaultCapabilities { get; } = new(
        ConnectionProtocol.Memory,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        Security: ConnectionSecurity.None);

    /// <summary>
    /// Gets the far-end writer; bytes written here arrive on the connection's <see cref="Input"/>.
    /// </summary>
    public PipeWriter PeerOutput => _receivePipe.Writer;

    /// <summary>
    /// Gets the far-end reader; it observes the bytes the connection wrote to <see cref="Output"/>.
    /// </summary>
    public PipeReader PeerInput => _sendPipe.Reader;

    public bool IsAborted { get; private set; }

    public bool IsDisposed { get; private set; }

    public Exception? AbortReason { get; private set; }

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 11000);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 12000);

    public override PipeReader Input => _receivePipe.Reader;

    public override PipeWriter Output => _sendPipe.Writer;

    public override ConnectionDirection Direction => _direction;

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
        _receivePipe.Writer.Complete();
        _receivePipe.Reader.Complete();
        _sendPipe.Writer.Complete();
        _sendPipe.Reader.Complete();
        _closedSource.Cancel();
        return ValueTask.CompletedTask;
    }
}
