using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

/// <summary>
/// Represents a UDP transport connection.
/// </summary>
public sealed class UdpTransportConnection : SingleStreamTransportConnection<UdpTransportConnectionContext>
{
    private const int DatagramBufferSize = 65_535;

    private readonly Socket? _socket;
    private readonly bool _ownsSocket;
    private readonly bool _hasReceiveLoop;
    private readonly TransportPipeOptionsContext _pipeOptions;
    private readonly Pipe _receivePipe;
    private readonly Pipe _sendPipe;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> _sendAsync;
    private readonly CancellationTokenSource _shutdownTokenSource;
    private readonly Lock _stateLock;

    private volatile ConnectionState _state;
    private Task? _receiveTask;
    private Task? _sendTask;
    private int _isOpen;
    private bool _isDisposed;

    internal UdpTransportConnection(
        Socket socket,
        TransportId transportId,
        TransportPipeline<UdpTransportConnectionContext> pipeline,
        TransportPipeOptionsContext pipeOptions,
        bool ownsSocket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(pipeOptions);

        _socket = socket;
        _ownsSocket = ownsSocket;
        _hasReceiveLoop = true;
        _pipeOptions = pipeOptions;
        Pipeline = pipeline;
        _sendAsync = (buffer, cancellationToken) => socket.SendAsync(buffer, SocketFlags.None, cancellationToken);
        _shutdownTokenSource = new CancellationTokenSource();
        _stateLock = new Lock();
        _state = ConnectionState.Idle;

        _receivePipe = new Pipe(pipeOptions.InputOptions);
        _sendPipe = new Pipe(pipeOptions.OutputOptions);

        Context = new UdpTransportConnectionContext(
            socket.LocalEndPoint!,
            socket.RemoteEndPoint!,
            new TransportConnectionPipe(_receivePipe.Reader, _sendPipe.Writer));

        TransportId = transportId;
    }

    internal UdpTransportConnection(
        TransportId transportId,
        TransportPipeline<UdpTransportConnectionContext> pipeline,
        EndPoint localEndPoint,
        EndPoint remoteEndPoint,
        TransportPipeOptionsContext pipeOptions,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> sendAsync)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(pipeOptions);
        ArgumentNullException.ThrowIfNull(sendAsync);

        _socket = null;
        _ownsSocket = false;
        _hasReceiveLoop = false;
        _pipeOptions = pipeOptions;
        Pipeline = pipeline;
        _sendAsync = sendAsync;
        _shutdownTokenSource = new CancellationTokenSource();
        _stateLock = new Lock();
        _state = ConnectionState.Idle;

        _receivePipe = new Pipe(pipeOptions.InputOptions);
        _sendPipe = new Pipe(pipeOptions.OutputOptions);

        Context = new UdpTransportConnectionContext(
            localEndPoint,
            remoteEndPoint,
            new TransportConnectionPipe(_receivePipe.Reader, _sendPipe.Writer));

        TransportId = transportId;
    }

    /// <inheritdoc />
    public override ConnectionId Id { get; } = ConnectionId.New();

    /// <inheritdoc />
    public override TransportId TransportId { get; }

    /// <inheritdoc />
    public override TransportProtocol Protocol { get; } = TransportProtocol.Udp;

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    protected override TransportPipeline<UdpTransportConnectionContext>? Pipeline { get; }

    /// <summary>
    /// Gets the strongly typed UDP connection context.
    /// </summary>
    public UdpTransportConnectionContext Context { get; }

    internal Action? OnDispose { get; set; }

    /// <inheritdoc />
    public override UdpTransportConnectionContext Open()
    {
        return OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async ValueTask<UdpTransportConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Interlocked.CompareExchange(ref _isOpen, 1, 0) != 0)
        {
            throw new InvalidOperationException("The UDP connection is already open.");
        }

        lock (_stateLock)
        {
            _state = ConnectionState.Opening;
        }

        _sendTask = SendAsync(_shutdownTokenSource.Token);
        _receiveTask = _hasReceiveLoop ? ReceiveFromSocketAsync(_shutdownTokenSource.Token) : Task.CompletedTask;

        lock (_stateLock)
        {
            _state = ConnectionState.Open;
        }

        TransportEventSource.Log.TransportConnectionStart(Protocol, TransportId, Id);

        if (Pipeline is not null)
        {
            await Pipeline.ExecuteAsync(Context, cancellationToken).ConfigureAwait(false);
        }

        return Context;
    }

    /// <inheritdoc />
    public override async ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            _state = ConnectionState.Aborted;
        }

        _shutdownTokenSource.Cancel();

        if (_socket is not null)
        {
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            if (_ownsSocket)
            {
                _socket.Dispose();
            }
        }

        CompletePipeWriter(_sendPipe.Writer);

        if (!_hasReceiveLoop)
        {
            CompletePipeWriter(_receivePipe.Writer);
        }

        if (_receiveTask is not null)
        {
            await AwaitWithoutException(_receiveTask).ConfigureAwait(false);
        }

        if (_sendTask is not null)
        {
            await AwaitWithoutException(_sendTask).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            _state = ConnectionState.Closed;
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        await AbortAsync().ConfigureAwait(false);

        _shutdownTokenSource.Dispose();
        _pipeOptions.Dispose();

        OnDispose?.Invoke();
    }

    internal async ValueTask EnqueueInboundDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || datagram.IsEmpty)
        {
            return;
        }

        try
        {
            _receivePipe.Writer.Write(datagram.Span);
            _ = await _receivePipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task ReceiveFromSocketAsync(CancellationToken cancellationToken)
    {
        Exception? error = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Memory<byte> memory = _receivePipe.Writer.GetMemory(DatagramBufferSize);
                int bytesRead = await _socket!.ReceiveAsync(memory, SocketFlags.None, cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    TransportEventSource.Log.TransportConnectionFinished(Protocol, TransportId, Id);
                    break;
                }

                _receivePipe.Writer.Advance(bytesRead);
                FlushResult result = await _receivePipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.OperationAborted or SocketError.Interrupted)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            error = exception;
            TransportEventSource.Log.TransportConnectionError(Protocol, TransportId, Id, exception.Message);
        }
        finally
        {
            await _receivePipe.Writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        Exception? error = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await _sendPipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (!buffer.IsEmpty)
                {
                    foreach (ReadOnlyMemory<byte> segment in buffer)
                    {
                        if (!segment.IsEmpty)
                        {
                            _ = await _sendAsync(segment, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                _sendPipe.Reader.AdvanceTo(buffer.End);

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            error = exception;
            TransportEventSource.Log.TransportConnectionError(Protocol, TransportId, Id, exception.Message);
        }
        finally
        {
            await _sendPipe.Reader.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(UdpTransportConnection));
    }

    private static void CompletePipeWriter(PipeWriter writer)
    {
        try
        {
            writer.Complete();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task AwaitWithoutException(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
