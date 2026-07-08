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
/// A reliable, ordered, single-stream <see cref="Connection"/> over a connected named-pipe stream
/// (<see cref="NamedPipeServerStream"/> on the accept side, <see cref="NamedPipeClientStream"/> on the
/// dial side).
/// </summary>
/// <remarks>
/// <para>
/// The duplex pipe is bridged over the pipe stream with <see cref="PipeReader.Create(Stream, StreamPipeReaderOptions)"/>
/// and <see cref="PipeWriter.Create(Stream, StreamPipeWriterOptions)"/> — pull-based adapters with no
/// background pump loop, so the connection is allocation-light and trim-safe. The stream is left open by
/// the reader/writer and disposed once, by this connection, on tear-down.
/// </para>
/// <para>
/// <see cref="ConnectionClosed"/> fires when this end is disposed or aborted; a peer close is observed by
/// reading <see cref="Input"/> (which completes) or writing <see cref="Output"/> (whose flush faults),
/// exactly as a byte-stream consumer such as an HTTP parser already detects end-of-connection.
/// </para>
/// </remarks>
internal sealed class NamedPipeConnection : Connection
{
    private static readonly ConnectionCapabilities NamedPipeCapabilities = new(
        ConnectionProtocol.NamedPipe,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    private readonly PipeStream _stream;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly EndPoint? _localEndPoint;
    private readonly EndPoint? _remoteEndPoint;
    private readonly ConnectionId _id = ConnectionId.New();
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _gate = new();

    private ConnectionState _state = ConnectionState.Open;
    private bool _isDisposed;

    public NamedPipeConnection(PipeStream stream, EndPoint? localEndPoint, EndPoint? remoteEndPoint)
    {
        _stream = stream;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;

        // Leave the stream open: this connection owns the stream and disposes it exactly once on
        // tear-down, so neither the reader nor the writer should close it underneath.
        _input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        _output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
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
    public override ConnectionCapabilities Capabilities => NamedPipeCapabilities;

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
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            _state = ConnectionState.Aborted;
        }

        // Completing the halves with the abort reason makes an in-flight read/write on this end observe
        // it; disposing the stream tears the pipe down so the peer's next read/write faults.
        Exception abortReason = reason ?? new ConnectionAbortedException();

        CompleteWriter(abortReason);
        CompleteReader(abortReason);
        DisposeStream();
        CancelConnectionClosed();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        bool wasAborted;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            wasAborted = _state == ConnectionState.Aborted;
        }

        // A graceful dispose completes both halves without an error and closes the pipe, so the peer
        // observes end-of-stream.
        CompleteWriter(null);
        CompleteReader(null);

        await _stream.DisposeAsync().ConfigureAwait(false);

        if (!wasAborted)
        {
            lock (_gate)
            {
                _state = ConnectionState.Closed;
            }
        }

        CancelConnectionClosed();
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

    private void CancelConnectionClosed()
    {
        try
        {
            _connectionClosedSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The token source was disposed concurrently with tear-down.
        }
    }
}
