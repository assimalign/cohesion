using System;
using System.IO;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Connections.NamedPipes.Internal;

/// <summary>
/// A reliable, ordered, single-stream <see cref="DuplexConnection"/> over a connected named-pipe stream
/// (<see cref="NamedPipeServerStream"/> on the accept side, <see cref="NamedPipeClientStream"/> on the
/// dial side).
/// </summary>
/// <remarks>
/// <para>
/// A receive pump copies the pipe stream into a bounded input pipe and observes peer closure.
/// The output uses a stream-backed writer. The connection owns the native pipe stream.
/// </para>
/// <para>
/// <see cref="ConnectionClosed"/> fires on local disposal, abort, and peer closure observed by the
/// receive pump, including while the application performs no reads or writes.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("NamedPipeConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class NamedPipeConnection : DuplexConnection
{
    private static readonly ConnectionCapabilities _namedPipeCapabilities = new(
        ConnectionProtocol.NamedPipe,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    private readonly PipeStream _stream;
    private readonly Pipe _incoming = new();
    private readonly Task _receiveTask;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly EndPoint? _localEndPoint;
    private readonly EndPoint? _remoteEndPoint;
    private readonly ConnectionId _id = ConnectionId.New();
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _gate = new();

    private ConnectionState _state = ConnectionState.Open;
    private bool _isDisposed;
    private int _closeReported;

    /// <summary>
    /// Wraps a connected pipe stream.
    /// </summary>
    /// <param name="stream">The connected pipe stream; the connection takes ownership.</param>
    /// <param name="listenerId">The accepting listener, or <see cref="ListenerId.Empty"/> for a dialed connection.</param>
    /// <param name="localEndPoint">The local endpoint, when known.</param>
    /// <param name="remoteEndPoint">The remote endpoint, when known.</param>
    public NamedPipeConnection(PipeStream stream, ListenerId listenerId, EndPoint? localEndPoint, EndPoint? remoteEndPoint)
    {
        _stream = stream;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;

        // Leave the stream open: this connection owns the stream and disposes it exactly once on
        // tear-down, so neither the reader nor the writer should close it underneath.
        _input = _incoming.Reader;
        _output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));

        NamedPipeConnectionEventSource.Log.ConnectionOpened(_id, listenerId, localEndPoint, remoteEndPoint);
        _receiveTask = ReceiveAsync();
    }

    /// <inheritdoc />
    public override ConnectionId Id => _id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _localEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _remoteEndPoint;

    /// <inheritdoc />
    public override PipeReader Input => _input;

    /// <inheritdoc />
    public override PipeWriter Output => _output;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities => _namedPipeCapabilities;

    /// <inheritdoc />
    public override ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedSource.Token;

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        bool firstAbort;
        lock (_gate)
        {
            firstAbort = _state is not (ConnectionState.Aborted or ConnectionState.Closed);
            if (firstAbort)
            {
                _state = ConnectionState.Aborted;
            }
        }

        if (!firstAbort)
        {
            CancelConnectionClosed();
            return;
        }

        // Completing the halves with the abort reason makes an in-flight read/write on this end observe
        // it; disposing the stream tears the pipe down so the peer's next read/write faults.
        Exception abortReason = reason ?? new ConnectionAbortedException();

        CompleteWriter(abortReason);
        CompleteReader(abortReason);
        _incoming.Writer.CancelPendingFlush();
        DisposeStream();
        ReportClosed();
        CancelConnectionClosed();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        bool wasAborted;
        bool alreadyDisposed;

        lock (_gate)
        {
            alreadyDisposed = _isDisposed;
            _isDisposed = true;
            wasAborted = _state == ConnectionState.Aborted;
        }

        if (alreadyDisposed)
        {
            CancelConnectionClosed();
            return;
        }

        // A graceful dispose completes both halves without an error and closes the pipe, so the peer
        // observes end-of-stream.
        CompleteWriter(null);
        CompleteReader(null);

        _incoming.Writer.CancelPendingFlush();
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _receiveTask.ConfigureAwait(false);

        if (!wasAborted)
        {
            lock (_gate)
            {
                _state = ConnectionState.Closed;
            }
        }

        ReportClosed();
        CancelConnectionClosed();
    }

    private async Task ReceiveAsync()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                int read = await _stream.ReadAsync(_incoming.Writer.GetMemory(4096)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _incoming.Writer.Advance(read);
                FlushResult flush = await _incoming.Writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCanceled || flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            error = exception;
        }
        finally
        {
            await _incoming.Writer.CompleteAsync(error).ConfigureAwait(false);
            lock (_gate)
            {
                if (_state == ConnectionState.Open)
                {
                    _state = error is null ? ConnectionState.Closed : ConnectionState.Aborted;
                }
            }

            ReportClosed();
            CancelConnectionClosed();
        }
    }

    private void CompleteWriter(Exception? exception)
    {
        try
        {
            _output.Complete(exception);
        }
        catch (InvalidOperationException)
        {
            // Already completed by the holder; nothing to do.
        }
    }

    private void CompleteReader(Exception? exception)
    {
        try
        {
            _input.Complete(exception);
        }
        catch (InvalidOperationException)
        {
            // Already completed by the holder; nothing to do.
        }
    }

    private void DisposeStream()
    {
        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
            // The pipe was already broken; the connection is being torn down regardless.
        }
    }

    // Abort and DisposeAsync both end the connection; whichever comes first reports it, once.
    private void ReportClosed()
    {
        if (Interlocked.Exchange(ref _closeReported, 1) == 0)
        {
            NamedPipeConnectionEventSource.Log.ConnectionClosed(_id);
        }
    }

    private void CancelConnectionClosed()
    {
        try
        {
            _connectionClosedSource.Cancel();
        }
        catch (AggregateException)
        {
            // Observer failures cannot prevent transport teardown.
        }
    }

    private sealed class DebugView
    {
        private readonly NamedPipeConnection _target;

        public DebugView(NamedPipeConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public PipeReader Input => _target.Input;
        public PipeWriter Output => _target.Output;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public ConnectionState State => _target.State;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
        public global::Assimalign.Cohesion.Connections.ConnectionDirection Direction => _target.Direction;
        public global::Assimalign.Cohesion.Connections.IConnectionFeatureCollection? InnerFeatures => _target.InnerFeatures;
    }
}
