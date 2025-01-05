using System;
using System.Net;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class SocketTransportConnection : ITransportConnection
{
    private readonly CancellationTokenSource connectionClosedTokenSource = new CancellationTokenSource();
    private readonly TaskCompletionSource connectionClosingProcess = new TaskCompletionSource();
    private readonly TransportTraceHandler trace;

    private volatile bool isSocketDisposed;
    private bool isConnectionClosed;
    private readonly object @lock = new object();

    private volatile ConnectionState state;


    public SocketTransportConnection(SocketTransportConnectionSettings? settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var serverPipe = new Pipe(settings.InputOptions);
        var clientPipe = new Pipe(settings.OutputOptions);

        if (settings.IsServer)
        {
            this.Pipe = new TransportConnectionPipe(
                serverPipe.Reader,
                clientPipe.Writer);

            this.Output = serverPipe.Writer;
            this.Input = clientPipe.Reader;
        }
        else
        {
            this.Pipe = new TransportConnectionPipe(
                clientPipe.Reader,
                serverPipe.Writer);

            this.Input = serverPipe.Reader;
            this.Output = clientPipe.Writer;
        }

        this.trace = settings.OnTrace;
        this.Socket = settings.Socket;
        this.LocalEndPoint = settings?.Socket.LocalEndPoint!;
        this.RemoteEndPoint = settings?.Socket.RemoteEndPoint!;
        this.SenderPool = new SocketPipeSenderPool(settings!.SenderScheduler);
        this.Receiver = new SocketPipeReceiver(settings.ReceiverScheduler);
    }


    public bool IsConnected => RemoteEndPoint is not null;
    public object? ConnectionData { get; set; }
    public ConnectionState State => this.state;
    public ITransportConnectionPipe Pipe { get; set; }
    public EndPoint LocalEndPoint { get; set; }
    public EndPoint RemoteEndPoint { get; set; }
    public Action OnDispose { get; set; } = default!;

    public readonly PipeWriter Output;
    public readonly PipeReader Input;
    public readonly SocketPipeReceiver Receiver;
    public readonly SocketPipeSenderPool SenderPool;
    public readonly Socket Socket;
    public Exception? ConnectionError;


    public void Abort()
    {
        AbortAsync().GetAwaiter().GetResult();
    }
    public ValueTask AbortAsync()
    {
        lock (@lock)
        {
            if (isSocketDisposed)
            {
                return ValueTask.CompletedTask;
            }

            // Make sure to close the connection only after the _aborted flag is set.
            // Without this, the RequestsCanBeAbortedMidRead test will sometimes fail when
            // a BadHttpRequestException is thrown instead of a TaskCanceledException.

            // ConnectionError should only be null if the output was completed gracefully, so no one should ever
            // ever observe the nondescript ConnectionAbortedException except for connection middleware attempting
            // to half close the connection which is currently unsupported.
            // _shutdownReason = ConnectionError ?? new ConnectionAbortedException("The Socket transport's send loop completed gracefully.");
            // _trace.ConnectionWriteFin(this, _shutdownReason.Message);

            try
            {
                // Try to gracefully close the socket even for aborts to match libuv behavior.
                Socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore any errors from Socket.Shutdown() since we're tearing down the connection anyway.
            }

            Socket.Dispose();
            state = ConnectionState.Aborted;
        }
        return ValueTask.CompletedTask;
    }
    public void Dispose()
    {
        if (!isSocketDisposed)
        {
            throw new ObjectDisposedException(nameof(SocketTransportConnection));
        }
        Abort();
        isSocketDisposed = true;
        OnDispose?.Invoke();
    }
    public void Execute()
    {
        if (state != ConnectionState.Running)
        {
            _ = Receive();
            _ = Send();

            state = ConnectionState.Running;
        }
    }
    public async Task Receive()
    {
        var error = default(Exception);

        try
        {
            while (true)
            {
                // Ensure we have some reasonable amount of buffer space
                var buffer = Output.GetMemory(PipelineMemoryPool.BlockSize / 2);
                var result = await Receiver.ReceiveAsync(Socket, buffer);

                if (result.BytesTransferred == 0)
                {
                    // FIN
                    trace?.Invoke(SocketTraceCode.Finished, ConnectionData, "The remote host has finished sending data.");
                    break;
                }

                Output.Advance(result.BytesTransferred);

                var flushResultTask = Output.FlushAsync();
                var flushResultTaskPaused = !flushResultTask.IsCompleted;

                if (flushResultTask.IsCompleted)
                {
                    // TODO: Add 'Connection Paused' Trace
                    trace?.Invoke(SocketTraceCode.Paused, ConnectionData, "The connection has been paused receiving data.");
                }

                var flushResult = await flushResultTask;

                if (flushResultTaskPaused)
                {
                    trace?.Invoke(SocketTraceCode.Resumed, ConnectionData, "The connection has resumed receiving data.");
                }
                if (flushResult.IsCompleted || flushResult.IsCanceled)
                {
                    // ClientPipe consumer is shut down, do we stop writing
                    break;
                }
            }
        }
        catch (SocketException exception) when (SocketHelper.IsConnectionResetError(exception.SocketErrorCode))
        {
            // This could be ignored if ConnectionError is already set.
            error ??= exception;

            // There's still a small chance that both DoReceive() and DoSend() can log the same connection reset.
            // Both logs will have the same ConnectionId. I don't think it's worthwhile to lock just to avoid this.
            if (!isSocketDisposed)
            {
                trace?.Invoke(SocketTraceCode.Reset, ConnectionData, exception.Message);
            }
        }
        catch (Exception exception)
        {
            // This exception should always be ignored because ConnectionError should be set.
            error = exception;

            if ((exception is SocketException socketException && SocketHelper.IsConnectionAbortError(socketException.SocketErrorCode)) || exception is ObjectDisposedException)
            {
                if (!isSocketDisposed)
                {
                    // This is unexpected if the Socket hasn't been disposed yet.
                    trace?.Invoke(SocketTraceCode.Error, ConnectionData, exception.Message);
                }
            }
            else
            {
                trace?.Invoke(ConnectionData, SocketTraceCode.Error, $"A connection error occurred while receiving data: {exception.Message}");
            }
        }
        finally
        {
            // If Shutdown() has already bee called, assume that was the reason ProcessReceives() exited.
            Output.Complete(ConnectionError ?? error);

            // Guard against scheduling this multiple times
            if (!isConnectionClosed)
            {
                isConnectionClosed = true;

                ThreadPool.UnsafeQueueUserWorkItem(state =>
                {
                    state.CancelConnectionClosedToken();
                    state.connectionClosingProcess.TrySetResult();

                }, this, preferLocal: false);

                await connectionClosingProcess.Task;

                Abort();
            }
        }
    }
    public async Task Send()
    {
        var error = default(Exception);

        try
        {
            while (true)
            {
                var result = await Input.ReadAsync();

                if (result.IsCanceled)
                {
                    break;
                }

                var buffer = result.Buffer;

                if (!buffer.IsEmpty)
                {
                    var sender = SenderPool.Rent();

                    sender.RemoteEndPoint ??= RemoteEndPoint;

                    var transferResult = default(SocketPipeResult);

                    switch (Socket.SocketType)
                    {
                        case SocketType.Stream: // Streams represent connection oriented sockets
                            transferResult = await sender.SendAsync(Socket, buffer);
                            break;
                        case SocketType.Dgram: // Dgrams represent connectionless sockets
                            transferResult = await sender.SendToAsync(Socket, buffer);
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

                    // We don't return to the pool if there was an exception, and
                    // we keep the sender assigned so that we can dispose it in StartAsync.
                    SenderPool.Return(sender);
                }

                Input.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (SocketException exception)
        when (SocketHelper.IsConnectionResetError(exception.SocketErrorCode))
        {
            error = new SocketConnectionResetException(exception.Message, exception);
            trace?.Invoke(SocketTraceCode.Reset, ConnectionData, $"The connection was reset for the following reason: {error.Message}");
        }
        catch (Exception exception)
        when ((exception is SocketException socketEx && SocketHelper.IsConnectionAbortError(socketEx.SocketErrorCode)) || exception is ObjectDisposedException)
        {
            // This should always be ignored since Shutdown() must have already been called by Abort().
            error = exception;
        }
        catch (Exception exception)
        {
            error = exception;
            trace?.Invoke(SocketTraceCode.Error, ConnectionData, $"A connection error occurred while sending data: {exception.Message}");
        }
        finally
        {
            Abort();

            // Complete the output after disposing the socket
            Input.Complete(error);

            // Cancel any pending flushes so that the input loop is un-paused
            Output.CancelPendingFlush();

            ConnectionError = error;
        }
    }
    private void CancelConnectionClosedToken()
    {
        try
        {
            connectionClosedTokenSource.Cancel();
        }
        catch (Exception)
        {
            //_trace.LogError(0, exception, $"Unexpected exception in {nameof(SocketConnection)}.{nameof(CancelConnectionClosedToken)}.");
        }
    }
}
