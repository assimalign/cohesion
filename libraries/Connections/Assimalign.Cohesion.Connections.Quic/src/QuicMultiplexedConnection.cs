using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// A QUIC connection that multiplexes independent streams, each surfaced as a <see cref="Connection"/>.
/// </summary>
/// <remarks>
/// The connection owns a single shared set of stream pipe options — one memory pool per
/// connection, not per stream — from which every accepted or opened stream creates its pipes.
/// Disposing the connection completes live bidirectional streams (delivering any in-flight
/// application data), then closes the underlying QUIC connection, and only then releases the
/// remaining unidirectional streams and the shared pool. Unidirectional streams are long-lived
/// control channels in multiplexed protocols — HTTP/3 treats its control and QPACK streams as
/// critical (RFC 9114 §6.2.1) — so the connection close must reach the peer before any
/// stream-level teardown signal for them.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicMultiplexedConnection : MultiplexedConnection
{
    private readonly QuicConnection _connection;
    private readonly ListenerId _listenerId;
    private readonly long _defaultStreamErrorCode;
    private readonly long _defaultCloseErrorCode;
    private readonly StreamPipeOptionsContext _streamOptions;
    private readonly ConcurrentDictionary<ConnectionId, QuicStreamConnection> _streams = new();
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _stateLock = new();

    private volatile ConnectionState _state;
    private bool _isDisposed;

    internal QuicMultiplexedConnection(
        QuicConnection connection,
        ListenerId listenerId,
        long defaultStreamErrorCode,
        long defaultCloseErrorCode,
        StreamPipeOptionsContext streamOptions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(streamOptions);

        _connection = connection;
        _listenerId = listenerId;
        _defaultStreamErrorCode = defaultStreamErrorCode;
        _defaultCloseErrorCode = defaultCloseErrorCode;
        _streamOptions = streamOptions;
        LocalEndPoint = connection.LocalEndPoint;
        RemoteEndPoint = connection.RemoteEndPoint;
        _state = ConnectionState.Open;

        ConnectionEventSource.Log.ConnectionStart(ConnectionProtocol.Quic, listenerId, Id);
    }

    /// <inheritdoc />
    public override ConnectionId Id { get; } = ConnectionId.New();

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Quic,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: true,
        ConnectionSecurity.Tls);

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedSource.Token;

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Thrown when the connection has been disposed.</exception>
    public override async ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        QuicStream stream = await _connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);

        return await WrapStreamAsync(stream).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="direction"/> is <see cref="ConnectionDirection.ReadOnly"/> or is
    /// not a defined <see cref="ConnectionDirection"/> value; a peer cannot open a stream that only
    /// the remote side writes to.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection has been disposed.</exception>
    public override async ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        QuicStreamType streamType = direction switch
        {
            ConnectionDirection.Bidirectional => QuicStreamType.Bidirectional,
            ConnectionDirection.WriteOnly => QuicStreamType.Unidirectional,
            _ => throw new ArgumentException("A peer cannot open a stream that only the remote side writes to.", nameof(direction))
        };

        QuicStream stream = await _connection.OpenOutboundStreamAsync(streamType, cancellationToken).ConfigureAwait(false);

        return await WrapStreamAsync(stream).ConfigureAwait(false);
    }

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

        // Abort is synchronous while closing a QUIC connection is asynchronous; fire and forget
        // the close, which observes its own faults so nothing surfaces as unobserved.
        _ = CloseConnectionAsync();

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

            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closing;
            }
        }

        // Teardown ordering is wire-visible and load-bearing. Bidirectional streams are
        // completed first: their write halves carry application data (an HTTP/3 response, for
        // example) whose graceful FIN and delivery must precede the connection close.
        // Unidirectional streams are deliberately NOT touched before the close — in multiplexed
        // protocols they are typically long-lived control channels (the HTTP/3 control and QPACK
        // streams are critical streams, and RFC 9114 §6.2.1 requires a peer that observes one
        // terminate to fail the whole connection with H3_CLOSED_CRITICAL_STREAM), so the
        // connection close must be the first teardown signal the peer sees for them.
        // ConcurrentDictionary.Values is a point-in-time snapshot; each stream untracks itself
        // through its dispose callback.
        foreach (QuicStreamConnection stream in _streams.Values)
        {
            if (stream.Direction == ConnectionDirection.Bidirectional)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        await CloseConnectionAsync().ConfigureAwait(false);

        // The connection is closed; disposing the remaining (unidirectional) streams releases
        // their pipes and handles without putting stream-level frames on the wire.
        foreach (QuicStreamConnection stream in _streams.Values)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);

        CancelConnectionClosedToken();

        ConnectionEventSource.Log.ConnectionStop(ConnectionProtocol.Quic, _listenerId, Id);

        // The stream options own the connection's shared memory pool; dispose them last, after
        // every stream and the connection itself have released their buffers.
        _streamOptions.Dispose();

        lock (_stateLock)
        {
            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }
        }
    }

    private async ValueTask<Connection> WrapStreamAsync(QuicStream stream)
    {
        QuicStreamConnection connection;

        try
        {
            connection = new QuicStreamConnection(
                stream,
                _listenerId,
                LocalEndPoint,
                RemoteEndPoint,
                _streamOptions,
                _defaultStreamErrorCode,
                OnStreamDisposed);
        }
        catch
        {
            // The stream options are connection-owned and shared by every stream, so a wrap
            // failure only releases the stream itself.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _streams.TryAdd(connection.Id, connection);

        return connection;
    }

    private void OnStreamDisposed(QuicStreamConnection stream)
    {
        _streams.TryRemove(stream.Id, out _);
    }

    private async Task CloseConnectionAsync()
    {
        try
        {
            await _connection.CloseAsync(_defaultCloseErrorCode).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The connection may already be disposed when an abort races a dispose.
        }
        catch (QuicException)
        {
            // The peer may have closed or reset the connection first; close failures are benign here.
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
