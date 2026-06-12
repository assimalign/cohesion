using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Transports.Tests;

/// <summary>
/// An in-memory <see cref="Connection"/> double built over two <see cref="Pipe"/> instances.
/// The far ends are exposed so a test can play the remote peer: bytes written to
/// <see cref="PeerOutput"/> appear on the connection's <see cref="Input"/>, and bytes the
/// connection writes to <see cref="Output"/> can be observed on <see cref="PeerInput"/>.
/// Use <see cref="CreatePair"/> to produce two cross-wired connections that act as each
/// other's remote peer.
/// </summary>
internal sealed class TestConnection : Connection
{
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionCapabilities _capabilities;
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;

    public TestConnection(ConnectionCapabilities? capabilities = null)
        : this(
            new Pipe(new PipeOptions(useSynchronizationContext: false)),
            new Pipe(new PipeOptions(useSynchronizationContext: false)),
            capabilities)
    {
    }

    private TestConnection(Pipe receivePipe, Pipe sendPipe, ConnectionCapabilities? capabilities)
    {
        _receivePipe = receivePipe;
        _sendPipe = sendPipe;
        _capabilities = capabilities ?? DefaultCapabilities;
    }

    /// <summary>
    /// Gets the default carrier capabilities accepted by the AMQP transports:
    /// a reliable, ordered, single-stream byte stream.
    /// </summary>
    public static ConnectionCapabilities DefaultCapabilities { get; } = new(
        ConnectionProtocol.Memory,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        Security: ConnectionSecurity.None);

    /// <summary>
    /// Creates two cross-wired connections: bytes written to one side's <see cref="Output"/>
    /// arrive on the other side's <see cref="Input"/>, and vice versa.
    /// </summary>
    public static (TestConnection Left, TestConnection Right) CreatePair(ConnectionCapabilities? capabilities = null)
    {
        Pipe leftToRight = new(new PipeOptions(useSynchronizationContext: false));
        Pipe rightToLeft = new(new PipeOptions(useSynchronizationContext: false));

        TestConnection left = new(rightToLeft, leftToRight, capabilities);
        TestConnection right = new(leftToRight, rightToLeft, capabilities);

        return (left, right);
    }

    /// <summary>
    /// Gets the far-end writer; bytes written here arrive on the connection's <see cref="Input"/>.
    /// </summary>
    public PipeWriter PeerOutput => _receivePipe.Writer;

    /// <summary>
    /// Gets the far-end reader; it observes the bytes the connection wrote to <see cref="Output"/>.
    /// </summary>
    public PipeReader PeerInput => _sendPipe.Reader;

    /// <summary>
    /// Gets a value indicating whether any unread bytes are buffered on the far-end reader.
    /// </summary>
    public bool HasBufferedPeerBytes
    {
        get
        {
            if (_sendPipe.Reader.TryRead(out ReadResult result))
            {
                bool hasBytes = result.Buffer.Length > 0;
                _sendPipe.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
                return hasBytes;
            }

            return false;
        }
    }

    public bool IsAborted { get; private set; }

    public bool IsDisposed { get; private set; }

    public Exception? AbortReason { get; private set; }

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5672);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 45678);

    public override PipeReader Input => _receivePipe.Reader;

    public override PipeWriter Output => _sendPipe.Writer;

    public override ConnectionCapabilities Capabilities => _capabilities;

    public override ConnectionState State => _state;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    /// <summary>
    /// Plays the remote peer by writing bytes that arrive on the connection's <see cref="Input"/>.
    /// </summary>
    public async Task WritePeerAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        await _receivePipe.Writer.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Completes the far-end writer, signaling end-of-stream on the connection's <see cref="Input"/>.
    /// </summary>
    public void CompletePeerOutput()
    {
        _receivePipe.Writer.Complete();
    }

    /// <summary>
    /// Reads everything currently buffered on the far-end reader.
    /// </summary>
    public async Task<byte[]> ReadBufferedPeerBytesAsync(CancellationToken cancellationToken = default)
    {
        ReadResult result = await _sendPipe.Reader.ReadAsync(cancellationToken);
        byte[] bytes = result.Buffer.ToArray();
        _sendPipe.Reader.AdvanceTo(result.Buffer.End);
        return bytes;
    }

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
