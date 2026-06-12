using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Internal;
using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp;

/// <summary>
/// A reliable, ordered, single-stream connection over a connected TCP socket.
/// </summary>
/// <remarks>
/// The connection is live on construction: the constructor starts the receive and send pump
/// loops, which continuously move bytes between the socket and the consumer-facing duplex pipe.
/// </remarks>
internal sealed class TcpConnection : Connection
{
    private readonly CancellationTokenSource _connectionClosedTokenSource = new CancellationTokenSource();
    private readonly TaskCompletionSource _connectionClosingProcess = new TaskCompletionSource();

    private readonly DuplexPipePair _pair;
    private readonly SocketPipeReceiver _receiver;
    private readonly SocketPipeSenderPool _senderPool;
    private readonly Socket _socket;
    private readonly ListenerId _listenerId;
    private readonly IDisposable? _ownedResource;
    private readonly int _memoryPoolBlockSize;
    private readonly Lock _lock = new();

    private Exception? _connectionError;
    private bool _isConnectionClosed;
    private bool _isDisposed;
    private volatile bool _isSocketDisposed;
    private volatile ConnectionState _state;

    internal TcpConnection(
        Socket socket,
        TcpConnectionSettings settings,
        ListenerId listenerId,
        IDisposable? ownedResource = null)
    {
        _socket = socket;
        _listenerId = listenerId;
        _ownedResource = ownedResource;

        LocalEndPoint = socket.LocalEndPoint;
        RemoteEndPoint = socket.RemoteEndPoint;

        _pair = DuplexPipePair.Create(settings.PipeOptions.InputOptions, settings.PipeOptions.OutputOptions);
        _receiver = new SocketPipeReceiver(settings.PipeOptions.ReceiverScheduler);
        _senderPool = new SocketPipeSenderPool(settings.PipeOptions.SenderScheduler);
        _memoryPoolBlockSize = settings.PipeOptions.BlockSize;

        _state = ConnectionState.Opening;

        _ = ReceiveAsync();
        _ = SendAsync();

        _state = ConnectionState.Open;

        ConnectionEventSource.Log.ConnectionStart(ConnectionProtocol.Tcp, listenerId, Id);
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
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Tcp,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedTokenSource.Token;

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        lock (_lock)
        {
            if (_isSocketDisposed)
            {
                return;
            }

            _isSocketDisposed = true;
            _connectionError ??= reason;

            try
            {
                // Try to gracefully close the socket even for aborts to match libuv behavior.
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Ignore any errors from Socket.Shutdown() since the connection is being torn down anyway.
            }
            catch (ObjectDisposedException)
            {
                // Ignore any errors from Socket.Shutdown() since the connection is being torn down anyway.
            }

            _socket.Dispose();
            _state = ConnectionState.Aborted;
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // An abort before disposal is a hard tear-down and the connection stays Aborted; a
        // dispose-initiated close transitions to Closed once the pump loops have finished.
        bool wasAborted = _state == ConnectionState.Aborted;

        Abort();

        await _connectionClosingProcess.Task.ConfigureAwait(false);

        _ownedResource?.Dispose();

        if (!wasAborted)
        {
            _state = ConnectionState.Closed;
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
                    ConnectionEventSource.Log.ConnectionFinished(ConnectionProtocol.Tcp, _listenerId, Id);
                    break;
                }

                _pair.TransportOutput.Advance(result.BytesTransferred);

                ValueTask<FlushResult> flushResultTask = _pair.TransportOutput.FlushAsync();
                bool flushResultTaskPaused = !flushResultTask.IsCompleted;

                if (flushResultTaskPaused)
                {
                    // Paused: the consumer is applying back-pressure, so receiving is paused.
                    ConnectionEventSource.Log.ConnectionPaused(ConnectionProtocol.Tcp, _listenerId, Id);
                }

                FlushResult flushResult = await flushResultTask;

                if (flushResultTaskPaused)
                {
                    // Resumed: the consumer caught up and the connection has resumed receiving data.
                    ConnectionEventSource.Log.ConnectionResumed(ConnectionProtocol.Tcp, _listenerId, Id);
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
                ConnectionEventSource.Log.ConnectionReset(ConnectionProtocol.Tcp, _listenerId, Id);
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
                    ConnectionEventSource.Log.ConnectionError(ConnectionProtocol.Tcp, _listenerId, Id, exception.Message);
                }
            }
            else
            {
                ConnectionEventSource.Log.ConnectionError(
                    ConnectionProtocol.Tcp,
                    _listenerId,
                    Id,
                    $"A connection error occurred while receiving data: {exception.Message}");
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

                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    state.CancelConnectionClosedToken();
                    state._connectionClosingProcess.TrySetResult();

                }, this, preferLocal: false);

                await _connectionClosingProcess.Task.ConfigureAwait(false);

                Abort();
            }
        }
    }

    private async Task SendAsync()
    {
        Exception? error = null;

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
                    break;
                }
            }
        }
        catch (SocketException exception)
        when (SocketHelper.IsConnectionResetError(exception.SocketErrorCode))
        {
            error = new ConnectionResetException(exception.Message, exception);
            ConnectionEventSource.Log.ConnectionReset(ConnectionProtocol.Tcp, _listenerId, Id);
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
            ConnectionEventSource.Log.ConnectionError(
                ConnectionProtocol.Tcp,
                _listenerId,
                Id,
                $"A connection error occurred while sending data: {exception.Message}");
        }
        finally
        {
            // Abort with the send error so it is recorded as the connection error before the
            // receive loop completes the transport output.
            Abort(error);

            // Complete the transport input after disposing the socket.
            _pair.TransportInput.Complete(error);

            // Cancel any pending flushes so that the receive loop is un-paused.
            _pair.TransportOutput.CancelPendingFlush();
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
}
