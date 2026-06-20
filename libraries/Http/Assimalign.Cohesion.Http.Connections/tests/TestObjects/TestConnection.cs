using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// An in-memory <see cref="Connection"/> double built over two cross-wired
/// <see cref="Pipe"/> instances. The supplied payload is preloaded onto the
/// connection's <see cref="Input"/> (as if the remote peer had already sent
/// it), and everything the holder writes to <see cref="Output"/> can be
/// observed via <see cref="ReadOutputAsync"/>.
/// </summary>
internal sealed class TestConnection : Connection
{
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly DrainSignalingPipeWriter _output;
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionDirection _direction;
    private readonly ConnectionCapabilities _capabilities;
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;

    public TestConnection(
        byte[]? input = null,
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        ConnectionCapabilities? capabilities = null,
        EndPoint? localEndPoint = null,
        EndPoint? remoteEndPoint = null)
    {
        _direction = direction;
        _capabilities = capabilities ?? DefaultCapabilities;
        LocalEndPoint = localEndPoint ?? new IPEndPoint(IPAddress.Loopback, 8080);
        RemoteEndPoint = remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 5000);

        // Pipes default to pauseWriterThreshold = 64 KB, which blocks the
        // synchronous prime write below for any input larger than that.
        // Tests for flow-control / large-frame scenarios need bigger
        // buffers; disable the threshold so all test payloads land
        // immediately.
        PipeOptions pipeOptions = new(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false);
        _receivePipe = new Pipe(pipeOptions);
        _sendPipe = new Pipe(pipeOptions);
        _output = new DrainSignalingPipeWriter(this, _sendPipe.Writer);

        if (input is { Length: > 0 })
        {
            _receivePipe.Writer.WriteAsync(input).GetAwaiter().GetResult();
        }

        // The peer has sent everything it ever will; the parser observes a
        // finite stream that ends after the preloaded payload.
        _receivePipe.Writer.Complete();
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

    public override EndPoint? LocalEndPoint { get; }

    public override EndPoint? RemoteEndPoint { get; }

    public override PipeReader Input => _receivePipe.Reader;

    public override PipeWriter Output => _output;

    public override ConnectionDirection Direction => _direction;

    public override ConnectionCapabilities Capabilities => _capabilities;

    public override ConnectionState State => _state;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    /// <summary>
    /// Reads the bytes the connection holder has written to <see cref="Output"/> so far.
    /// </summary>
    public async Task<byte[]> ReadOutputAsync()
    {
        ReadResult result = await _sendPipe.Reader.ReadAsync();
        byte[] output = result.Buffer.ToArray();
        _sendPipe.Reader.AdvanceTo(result.Buffer.End);
        return output;
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
        if (IsDisposed)
        {
            return ValueTask.CompletedTask;
        }

        IsDisposed = true;
        _state = ConnectionState.Closed;
        _receivePipe.Reader.Complete();
        // The send pipe's WRITER is completed so no further bytes can be
        // queued, but its READER is intentionally left open: tests inspect
        // the captured wire output (via ReadOutputAsync) after disposing
        // the HTTP connection that owned this transport connection.
        _sendPipe.Writer.Complete();
        _closedSource.Cancel();
        return ValueTask.CompletedTask;
    }

    private void OnOutputCompleted()
    {
        // A real transport's send loop drains its backlog and tears the
        // connection down once the holder completes the output writer. The
        // double mirrors that by leaving ConnectionState.Open immediately,
        // which lets the protocol connections' bounded drain-wait
        // (WaitForTransportDrainAsync) observe the transition without
        // burning its timeout.
        if (_state == ConnectionState.Open)
        {
            _state = ConnectionState.Closing;
        }
    }

    /// <summary>
    /// A delegating <see cref="PipeWriter"/> that signals the owning
    /// <see cref="TestConnection"/> when the holder completes the output,
    /// emulating a transport send loop's post-drain state transition.
    /// </summary>
    private sealed class DrainSignalingPipeWriter : PipeWriter
    {
        private readonly TestConnection _connection;
        private readonly PipeWriter _inner;

        public DrainSignalingPipeWriter(TestConnection connection, PipeWriter inner)
        {
            _connection = connection;
            _inner = inner;
        }

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => _inner.FlushAsync(cancellationToken);

        public override void Complete(Exception? exception = null)
        {
            _inner.Complete(exception);
            _connection.OnOutputCompleted();
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            await _inner.CompleteAsync(exception).ConfigureAwait(false);
            _connection.OnOutputCompleted();
        }
    }
}
