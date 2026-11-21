using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// A reliable, ordered, single-stream connection over a connected TCP socket.
/// </summary>
/// <remarks>
/// <para>
/// The connection is live on construction: the constructor starts the receive and send pump
/// loops, which continuously move bytes between the socket and the consumer-facing duplex pipe.
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> closes gracefully: it completes both consumer-facing pipes, the send
/// loop hands what the application already wrote to the socket and then shuts the socket down in
/// its own <c>finally</c>, and the close escalates to <see cref="Abort"/> only when the send loop
/// cannot drain within the close timeout (#1086). <see cref="Abort"/> tears the socket down at once
/// and discards whatever has not been sent.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("TcpConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class TcpConnection : DuplexConnection
{
    private readonly CancellationTokenSource _connectionClosedTokenSource = new CancellationTokenSource();
    private readonly TaskCompletionSource _connectionClosingProcess = new TaskCompletionSource();

    private readonly DuplexPipePair _pair;
    private readonly Task _receiveTask;
    private readonly Task _sendTask;
    private readonly SocketPipeReceiver _receiver;
    private readonly SocketPipeSenderPool _senderPool;
    private readonly Socket _socket;
    private readonly ConnectionProtocol _protocol;
    private readonly IDisposable? _ownedResource;
    private readonly int _memoryPoolBlockSize;
    private readonly TimeSpan _closeTimeout;
    private readonly Lock _lock = new();

    private Exception? _connectionError;
    private bool _isConnectionClosed;
    private int _disposeStarted;
    private volatile bool _isClosing;
    private volatile bool _isSocketDisposed;
    private volatile ConnectionState _state;

    internal TcpConnection(
        Socket socket,
        TcpConnectionSettings settings,
        ListenerId listenerId,
        IDisposable? ownedResource = null)
    {
        _socket = socket;
        _protocol = SocketConnectionProtocol.FromAddressFamily(socket.AddressFamily);
        _ownedResource = ownedResource;

        LocalEndPoint = socket.LocalEndPoint;
        RemoteEndPoint = socket.RemoteEndPoint;

        Capabilities = new ConnectionCapabilities(
            _protocol,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: false,
            ConnectionSecurity.None);

        _pair = DuplexPipePair.Create(settings.PipeOptions.InputOptions, settings.PipeOptions.OutputOptions);
        _receiver = new SocketPipeReceiver(settings.PipeOptions.ReceiverScheduler);
        _senderPool = new SocketPipeSenderPool(settings.PipeOptions.SenderScheduler);
        _memoryPoolBlockSize = settings.PipeOptions.BlockSize;
        _closeTimeout = settings.CloseTimeout;

        _state = ConnectionState.Opening;

        // Reported before the pump loops start, so no loop event can precede it.
        TcpConnectionEventSource.Log.ConnectionOpened(Id, listenerId, _protocol, LocalEndPoint, RemoteEndPoint);

        _receiveTask = ReceiveAsync();
        _sendTask = SendAsync();

        _state = ConnectionState.Open;
    }

    /// <inheritdoc />
    public override ConnectionId Id { get; } = ConnectionId.New();

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public override PipeReader Input => _pair.Input;

    /// <inheritdoc />
    public override PipeWriter Output => _pair.Output;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ConnectionCapabilities.Protocol"/> reflects the socket's address family, so a
    /// connection over a Unix domain socket reports <see cref="ConnectionProtocol.UnixDomainSocket"/>.
    /// The delivery guarantees (reliable, ordered byte stream) are identical for both families.
    /// </remarks>
    public override ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedTokenSource.Token;

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        if (CloseSocket(reason, abort: true))
        {
            _pair.TransportInput.CancelPendingRead();
            _pair.TransportOutput.CancelPendingFlush();
        }

        CancelConnectionClosedToken();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A graceful close (RFC 9293 §3.6): nothing the application wrote before the call is discarded
    /// unless the send loop cannot hand it to the socket within the close timeout, because the peer
    /// stopped reading. Then the connection is aborted and stays <see cref="ConnectionState.Aborted"/>.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            CancelConnectionClosedToken();
            return;
        }

        lock (_lock)
        {
            // An abort before disposal is a hard tear-down, and the connection stays Aborted.
            if (_state == ConnectionState.Open)
            {
                _state = ConnectionState.Closing;
            }
        }

        // Set before the pipes complete, so a receive loop that stops because its input completed
        // leaves the socket to the send loop instead of aborting it.
        _isClosing = true;

        // Completing the application's output lets the send loop send what is already written and
        // then shut the socket down in its own finally. Completing the application's input releases
        // a receive loop paused on back-pressure. Both are no-ops when the application completed them.
        _pair.Output.Complete();
        _pair.Input.Complete();

        Task pumps = Task.WhenAll(_receiveTask, _sendTask);
        if (!pumps.IsCompleted)
        {
            try
            {
                await pumps.WaitAsync(_closeTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The peer stopped reading, so the send loop cannot drain: abort, which discards what
                // is still unsent and fails the pending send.
                Abort();
                await pumps.ConfigureAwait(false);
            }
        }

        _ownedResource?.Dispose();

        lock (_lock)
        {
            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }
        }
    }

    private async Task ReceiveAsync()
    {
        Exception? error = null;

        try
        {
            while (true)
            {
                // Ensure we have some reasonable amount of buffer space.
                Memory<byte> buffer = _pair.TransportOutput.GetMemory(_memoryPoolBlockSize / 2);
                SocketPipeResult result = await _receiver.ReceiveAsync(_socket, buffer);

                if (result.BytesTransferred == 0)
                {
                    // Finished: the remote host has finished sending data.
                    TcpConnectionEventSource.Log.ConnectionFinished(Id);
                    break;
                }

                _pair.TransportOutput.Advance(result.BytesTransferred);

                ValueTask<FlushResult> flushResultTask = _pair.TransportOutput.FlushAsync();
                bool flushResultTaskPaused = !flushResultTask.IsCompleted;

                if (flushResultTaskPaused)
                {
                    // Paused: the consumer is applying back-pressure, so receiving is paused.
                    TcpConnectionEventSource.Log.ConnectionPaused(Id);
                }

                FlushResult flushResult = await flushResultTask;

                if (flushResultTaskPaused)
                {
                    // Resumed: the consumer caught up and the connection has resumed receiving data.
                    TcpConnectionEventSource.Log.ConnectionResumed(Id);
                }
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                {
                    // The consumer side is shut down, so stop writing.
                    break;
                }
            }
        }
        catch (SocketException exception) when (SocketHelper.IsConnectionResetError(exception.SocketErrorCode))
        {
            // This could be ignored if the connection error is already set.
            error ??= exception;

            // There's still a small chance that both the receive and send loops log the same
            // connection reset. Both logs will have the same connection id; locking just to
            // avoid the duplicate is not worthwhile.
            if (!_isSocketDisposed)
            {
                TcpConnectionEventSource.Log.ConnectionReset(Id);
            }
        }
        catch (Exception exception)
        {
            // This exception should always be ignored because the connection error should be set.
            error = exception;

            if ((exception is SocketException socketException && SocketHelper.IsConnectionAbortError(socketException.SocketErrorCode)) || exception is ObjectDisposedException)
            {
                if (!_isSocketDisposed)
                {
                    // This is unexpected if the socket hasn't been disposed yet.
                    TcpConnectionEventSource.Log.ConnectionError(Id, "receiving", exception);
                }
            }
            else
            {
                TcpConnectionEventSource.Log.ConnectionError(Id, "receiving", exception);
            }
        }
        finally
        {
            // If Shutdown() has already been called, assume that was the reason the receive loop exited.
            _pair.TransportOutput.Complete(_connectionError ?? error);

            // Guard against scheduling the close signal multiple times.
            if (!_isConnectionClosed)
            {
                _isConnectionClosed = true;

                // The one close report for this connection, made before the closing process completes so
                // that DisposeAsync never returns ahead of it.
                TcpConnectionEventSource.Log.ConnectionClosed(Id);

                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    state.CancelConnectionClosedToken();
                    state._connectionClosingProcess.TrySetResult();

                }, this, preferLocal: false);

                await _connectionClosingProcess.Task.ConfigureAwait(false);

                // During a graceful close the send loop owns the socket: it closes it once everything
                // the application wrote has been sent, and aborting here would discard that output. A
                // failed receive, or the connection ending outside a graceful close, still aborts.
                if (error is not null || !_isClosing)
                {
                    Abort();
                }
            }
        }
    }

    private async Task SendAsync()
    {
        Exception? error = null;
        bool drained = false;

        try
        {
            while (true)
            {
                ReadResult result = await _pair.TransportInput.ReadAsync();

                if (result.IsCanceled)
                {
                    break;
                }

                ReadOnlySequence<byte> buffer = result.Buffer;

                if (!buffer.IsEmpty)
                {
                    SocketPipeSender sender = _senderPool.Rent();

                    var transferResult = default(SocketPipeResult);

                    switch (_socket.SocketType)
                    {
                        case SocketType.Stream: // Streams represent connection oriented sockets
                            transferResult = await sender.SendAsync(_socket, buffer);
                            break;
                        default:
                            throw new NotSupportedException();
                    }
                    if (transferResult.HasError)
                    {
                        if (SocketHelper.IsConnectionResetError(transferResult.Error.SocketErrorCode))
                        {
                            error = transferResult.Error;
                            break;
                        }
                        if (SocketHelper.IsConnectionAbortError(transferResult.Error.SocketErrorCode))
                        {
                            error = transferResult.Error;
                            break;
                        }

                        error = transferResult.Error;
                    }

                    // We don't return to the pool if there was an exception so that a faulted
                    // sender is never reused.
                    _senderPool.Return(sender);
                }

                _pair.TransportInput.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    // The application completed its output and everything it wrote has been sent.
                    drained = error is null;
                    break;
                }
            }
        }
        catch (SocketException exception)
        when (SocketHelper.IsConnectionResetError(exception.SocketErrorCode))
        {
            error = new ConnectionResetException(exception.Message, exception);
            TcpConnectionEventSource.Log.ConnectionReset(Id);
        }
        catch (Exception exception)
        when ((exception is SocketException socketException && SocketHelper.IsConnectionAbortError(socketException.SocketErrorCode)) || exception is ObjectDisposedException)
        {
            // This should always be ignored since Shutdown() must have already been called by Abort().
            error = exception;
        }
        catch (Exception exception)
        {
            error = exception;
            TcpConnectionEventSource.Log.ConnectionError(Id, "sending", exception);
        }
        finally
        {
            if (drained)
            {
                // The bytes are in the socket's send buffer, so the shutdown queues the FIN behind them
                // and the kernel delivers both after the socket is closed (RFC 9293 §3.6).
                CloseSocket(reason: null, abort: false);
            }
            else
            {
                // Abort with the send error so it is recorded as the connection error before the
                // receive loop completes the transport output.
                Abort(error);
            }

            // Complete the transport input after disposing the socket.
            _pair.TransportInput.Complete(error);

            // Cancel any pending flushes so that the receive loop is un-paused.
            _pair.TransportOutput.CancelPendingFlush();
        }
    }

    /// <summary>
    /// Shuts the socket down in both directions and disposes it, once.
    /// </summary>
    /// <param name="reason">The connection error to record, when this call closes the socket.</param>
    /// <param name="abort">
    /// Whether this is a tear-down (<see cref="ConnectionState.Aborted"/>) rather than the send loop's
    /// close after it drained (<see cref="ConnectionState.Closing"/> until disposal completes).
    /// </param>
    /// <returns>Whether this call closed the socket.</returns>
    private bool CloseSocket(Exception? reason, bool abort)
    {
        lock (_lock)
        {
            if (_isSocketDisposed)
            {
                return false;
            }

            _isSocketDisposed = true;
            _connectionError ??= reason;
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // The connection is being torn down regardless of shutdown errors.
            }
            catch (ObjectDisposedException)
            {
            }

            _socket.Dispose();
            _state = abort ? ConnectionState.Aborted : ConnectionState.Closing;
            return true;
        }
    }

    private void CancelConnectionClosedToken()
    {
        try
        {
            _connectionClosedTokenSource.Cancel();
        }
        catch (AggregateException)
        {
            // A ConnectionClosed registration threw; the connection is closing regardless.
        }
        catch (ObjectDisposedException)
        {
            // The token source was disposed concurrently with tear-down.
        }
    }

    private sealed class DebugView
    {
        private readonly TcpConnection _target;

        public DebugView(TcpConnection value)
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
