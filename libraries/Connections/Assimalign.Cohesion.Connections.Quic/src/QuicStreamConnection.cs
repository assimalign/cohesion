using System;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Internal;
using Assimalign.Cohesion.Connections.Quic.Internal;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// A single QUIC stream surfaced as a <see cref="Connection"/>.
/// </summary>
/// <remarks>
/// The stream's pipes are created over the parent connection's shared stream pipe options, so
/// every stream of one QUIC connection draws from a single memory pool. A unidirectional stream
/// surfaces a pre-completed input (outbound) or an unwritable output (inbound); the usable
/// halves are captured once in <see cref="Direction"/> at construction.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicStreamConnection : Connection, IStreamIdentifierFeature
{
    private readonly QuicStream _stream;
    private readonly ListenerId _listenerId;
    private readonly long _defaultStreamErrorCode;
    private readonly Action<QuicStreamConnection> _onDisposed;
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _stateLock = new();

    private volatile ConnectionState _state;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new connection over the supplied QUIC stream. The connection takes ownership
    /// of the stream but not of the shared <paramref name="streamOptions"/>.
    /// </summary>
    /// <param name="stream">The QUIC stream to wrap.</param>
    /// <param name="listenerId">The identifier of the owning transport, for diagnostics.</param>
    /// <param name="localEndPoint">The parent connection's local endpoint.</param>
    /// <param name="remoteEndPoint">The parent connection's remote endpoint.</param>
    /// <param name="streamOptions">The parent-owned shared stream pipe options.</param>
    /// <param name="defaultStreamErrorCode">The error code used when the stream is aborted.</param>
    /// <param name="onDisposed">A callback invoked when the stream connection is disposed.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/>, <paramref name="streamOptions"/>, or
    /// <paramref name="onDisposed"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when the stream is neither readable nor writable.</exception>
    public QuicStreamConnection(
        QuicStream stream,
        ListenerId listenerId,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        StreamPipeOptionsContext streamOptions,
        long defaultStreamErrorCode,
        Action<QuicStreamConnection> onDisposed)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(streamOptions);
        ArgumentNullException.ThrowIfNull(onDisposed);

        Direction = (stream.CanRead, stream.CanWrite) switch
        {
            (true, true) => ConnectionDirection.Bidirectional,
            (true, false) => ConnectionDirection.ReadOnly,
            (false, true) => ConnectionDirection.WriteOnly,
            (false, false) => throw new ArgumentException("The QUIC stream is neither readable nor writable.", nameof(stream))
        };

        _stream = stream;
        _listenerId = listenerId;
        _defaultStreamErrorCode = defaultStreamErrorCode;
        _onDisposed = onDisposed;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        Input = stream.CanRead
            ? PipeReader.Create(stream, streamOptions.ReaderOptions)
            : PipeReader.Create(Stream.Null);
        Output = stream.CanWrite
            ? PipeWriter.Create(stream, streamOptions.WriterOptions)
            : UnwritablePipeWriter.Instance;
        _state = ConnectionState.Open;

        ConnectionEventSource.Log.ConnectionStart(ConnectionProtocol.Quic, listenerId, Id);
    }

    /// <inheritdoc />
    public override ConnectionId Id { get; } = ConnectionId.New();

    /// <inheritdoc />
    /// <remarks>
    /// The QUIC stream ID (RFC 9000 §2.1) the peer assigned to this stream, surfaced
    /// for consumers that must key wire-level bookkeeping on it (the HTTP/3 QPACK
    /// decoder keys Section Acknowledgment / Stream Cancellation on the request stream
    /// ID — RFC 9204 §4.4). It is distinct from the synthetic <see cref="Id"/>.
    /// </remarks>
    public long StreamId => _stream.Id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public override PipeReader Input { get; }

    /// <inheritdoc />
    public override PipeWriter Output { get; }

    /// <inheritdoc />
    public override ConnectionDirection Direction { get; }

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Quic,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.Tls);

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedSource.Token;

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        lock (_stateLock)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            _state = ConnectionState.Aborted;
        }

        _stream.Abort(QuicAbortDirection.Both, _defaultStreamErrorCode);

        CancelConnectionClosedToken();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        try
        {
            Output.Complete();
            Input.Complete();
        }
        catch (InvalidOperationException)
        {
            // Best-effort completion: ObjectDisposedException derives from InvalidOperationException,
            // so this single clause covers both an in-progress pipe operation and a disposed stream.
        }
        catch (IOException)
        {
            // Completing a pipe flushes any remaining buffered bytes into the QUIC stream, which
            // throws QuicException (an IOException) when the stream or its connection is already
            // closed — routine for streams released after their owning connection closed.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);

        CancelConnectionClosedToken();

        _onDisposed(this);

        ConnectionEventSource.Log.ConnectionStop(ConnectionProtocol.Quic, _listenerId, Id);

        lock (_stateLock)
        {
            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }
        }
    }

    private void CancelConnectionClosedToken()
    {
        try
        {
            _connectionClosedSource.Cancel();
        }
        catch (AggregateException)
        {
            // Exceptions thrown by ConnectionClosed registrations must not fault teardown.
        }
    }
}
