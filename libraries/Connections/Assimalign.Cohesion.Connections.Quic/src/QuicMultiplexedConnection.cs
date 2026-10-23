using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Quic.Internal;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// A QUIC connection that multiplexes independent streams, each surfaced as a <see cref="MultiplexedConnectionStream"/>.
/// </summary>
/// <remarks>
/// QUIC carries TLS 1.3 in its own handshake (RFC 9001), so the connection reports what that
/// handshake negotiated through <see cref="ITlsConnectionInfo"/>, captured when the connection is
/// produced (a QUIC connection is accepted or established only after its handshake completes).
/// The connection owns a single shared set of stream pipe options — one memory pool per
/// connection, not per stream — from which every accepted or opened stream creates its pipes.
/// Disposing the connection completes live bidirectional streams (delivering any in-flight
/// application data, for at most the options' <c>CloseDeliveryTimeout</c>, after which the close
/// ends a stream still delivering), then closes the underlying QUIC connection, and only then releases the
/// remaining unidirectional streams and the shared pool. Unidirectional streams are long-lived
/// control channels in multiplexed protocols — HTTP/3 treats its control and QPACK streams as
/// critical (RFC 9114 §6.2.1) — so the connection close must reach the peer before any
/// stream-level teardown signal for them.
/// <para>
/// The connection and its streams carry application error codes: <see cref="Abort(long, Exception)"/>
/// closes the connection with the caller's code, and every stream exposes a whole-stream reset through
/// <see cref="IMultiplexedConnectionStream.Abort(long, Exception)"/> and the optional <see cref="IMultiplexedStreamAbort"/>
/// directional-abort feature. <see cref="Abort(Exception)"/> and
/// <see cref="DisposeAsync"/> close with the configured default close code.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("QuicMultiplexedConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicMultiplexedConnection : MultiplexedConnection
{
    // RFC 9000 §16 — an application error code is a variable-length integer.
    private const long maxApplicationErrorCode = (1L << 62) - 1;
    private const long maxStreamCount = 1L << 60;

    private readonly QuicConnection _connection;
    private readonly Channel<MultiplexedConnectionStream> _inbound = Channel.CreateUnbounded<MultiplexedConnectionStream>();
    private readonly Task _acceptPump;
    private readonly long _defaultStreamErrorCode;
    private readonly long _defaultCloseErrorCode;
    private readonly TimeSpan _closeDeliveryTimeout;
    private readonly StreamPipeOptionsContext _streamOptions;
    private readonly Action _onInboundBidirectionalStreamReleaseStarted;
    private readonly ConcurrentDictionary<ConnectionId, QuicStreamConnection> _streams = new();
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _stateLock = new();

    private volatile ConnectionState _state;
    private bool _isDisposed;
    private long _closeErrorCode;
    private int _closeReported;
    private long _inboundBidirectionalStreamLimit;

    internal QuicMultiplexedConnection(
        QuicConnection connection,
        ListenerId listenerId,
        long defaultStreamErrorCode,
        long defaultCloseErrorCode,
        TimeSpan closeDeliveryTimeout,
        StreamPipeOptionsContext streamOptions,
        int maxInboundBidirectionalStreams)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(streamOptions);

        _connection = connection;
        _defaultStreamErrorCode = defaultStreamErrorCode;
        _defaultCloseErrorCode = defaultCloseErrorCode;
        _closeErrorCode = defaultCloseErrorCode;
        _closeDeliveryTimeout = closeDeliveryTimeout;
        _streamOptions = streamOptions;
        _inboundBidirectionalStreamLimit = maxInboundBidirectionalStreams;
        _onInboundBidirectionalStreamReleaseStarted = OnInboundBidirectionalStreamReleaseStarted;
        LocalEndPoint = connection.LocalEndPoint;
        RemoteEndPoint = connection.RemoteEndPoint;
        ApplicationProtocol = connection.NegotiatedApplicationProtocol;
        TlsProtocol = connection.SslProtocol;
        CipherSuite = connection.NegotiatedCipherSuite;
        RemoteCertificate = TakeRemoteCertificate(connection);
        _state = ConnectionState.Open;

        QuicConnectionEventSource.Log.ConnectionOpened(Id, listenerId, LocalEndPoint, RemoteEndPoint);
        _acceptPump = AcceptInboundStreamsAsync();
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

    /// <inheritdoc cref="ITlsConnectionInfo.ApplicationProtocol" />
    /// <remarks>QUIC requires ALPN (RFC 9001 §8.1), so an established connection always reports one.</remarks>
    public SslApplicationProtocol ApplicationProtocol { get; }

    /// <inheritdoc cref="ITlsConnectionInfo.TlsProtocol" />
    /// <remarks>QUIC carries TLS 1.3 (RFC 9001 §4), so this is <see cref="SslProtocols.Tls13"/>.</remarks>
    public SslProtocols TlsProtocol { get; }

    /// <inheritdoc cref="ITlsConnectionInfo.CipherSuite" />
    public TlsCipherSuite CipherSuite { get; }

    /// <inheritdoc cref="ITlsConnectionInfo.RemoteCertificate" />
    /// <remarks>
    /// The connection owns the certificate and disposes it when the connection is disposed.
    /// </remarks>
    public X509Certificate2? RemoteCertificate { get; }

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedSource.Token;

    /// <inheritdoc cref="IMultiplexedConnectionStreamLimits.InboundBidirectionalStreamLimit" />
    public long InboundBidirectionalStreamLimit => Volatile.Read(ref _inboundBidirectionalStreamLimit);

    /// <inheritdoc />
    protected override void ConfigureFeatures(IConnectionFeatureCollection features)
    {
        features.Set(new QuicTlsConnectionInfo(this));
        features.Set(new QuicConnectionStreamLimits(this));
    }

    internal int TrackedStreamCount => _streams.Count;

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Thrown when the connection has been disposed.</exception>
    public override async ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="direction"/> is <see cref="ConnectionDirection.ReadOnly"/> or is
    /// not a defined <see cref="ConnectionDirection"/> value; a peer cannot open a stream that only
    /// the remote side writes to.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection has been disposed.</exception>
    public override async ValueTask<MultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        QuicStreamType streamType = direction switch
        {
            ConnectionDirection.Bidirectional => QuicStreamType.Bidirectional,
            ConnectionDirection.WriteOnly => QuicStreamType.Unidirectional,
            _ => throw new ArgumentException(ErrorMessages.ReadOnlyStreamNotSupported, nameof(direction))
        };

        QuicStream stream = await _connection.OpenOutboundStreamAsync(streamType, cancellationToken).ConfigureAwait(false);

        return await WrapStreamAsync(stream, isInbound: false).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The connection closes with the configured default close code; <see cref="Abort(long, Exception)"/>
    /// chooses the code.
    /// </remarks>
    public override void Abort(Exception? reason = null)
    {
        AbortCore(_defaultCloseErrorCode);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The code is outside QUIC's range of 0 to 2^62 - 1.</exception>
    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode, maxApplicationErrorCode);

        AbortCore(errorCode);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        long closeErrorCode;
        bool alreadyDisposed;
        lock (_stateLock)
        {
            alreadyDisposed = _isDisposed;
            _isDisposed = true;
            closeErrorCode = _closeErrorCode;

            if (!alreadyDisposed && _state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closing;
            }
        }

        if (alreadyDisposed)
        {
            CancelConnectionClosedToken();
            return;
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
        //
        // A stream's disposal waits for the peer to acknowledge its data and FIN, which a peer that
        // withholds stream credit while keeping the connection alive never does. So every bidirectional
        // stream is disposed at once and the waits share one bound, CloseDeliveryTimeout; a stream still
        // delivering when it runs out is ended by the close below, which completes its disposal.
        Task? deliveries = DisposeStreams(bidirectionalOnly: true);

        if (deliveries is not null)
        {
            await WaitForDeliveryAsync(deliveries).ConfigureAwait(false);
        }

        await CloseConnectionAsync(closeErrorCode).ConfigureAwait(false);
        await _acceptPump.ConfigureAwait(false);

        // The close ended any stream still delivering. Release the remaining streams together,
        // including streams accepted after the first snapshot, within a second shared bound. Repeated
        // stream disposal waits for the same native release, so an unbounded final sweep would
        // undo the delivery timeout. Unidirectional streams are touched only after the close.
        Task? remaining = DisposeStreams(bidirectionalOnly: false);
        if (remaining is not null)
        {
            await WaitForDeliveryAsync(remaining).ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        while (_inbound.Reader.TryRead(out _))
        {
        }

        // Reading QuicConnection.RemoteCertificate handed its ownership to this connection (see
        // TakeRemoteCertificate), so the certificate is released here, with the connection.
        RemoteCertificate?.Dispose();

        CancelConnectionClosedToken();
        ReportClosed();

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

    /// <summary>
    /// Reads the certificate the peer presented. Reading <see cref="QuicConnection.RemoteCertificate"/>
    /// hands its ownership to the caller (a <see cref="QuicConnection"/> no longer disposes a
    /// certificate it has exposed), so this connection disposes it. The platform surfaces an
    /// <see cref="X509Certificate2"/>; any other instance is copied and released.
    /// </summary>
    private static X509Certificate2? TakeRemoteCertificate(QuicConnection connection)
    {
        X509Certificate? certificate = connection.RemoteCertificate;
        if (certificate is null or X509Certificate2)
        {
            return (X509Certificate2?)certificate;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        }
        finally
        {
            certificate.Dispose();
        }
    }

    private async Task AcceptInboundStreamsAsync()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                QuicStream stream = await _connection.AcceptInboundStreamAsync().ConfigureAwait(false);
                MultiplexedConnectionStream wrapped = await WrapStreamAsync(stream, isInbound: true).ConfigureAwait(false);
                if (!_inbound.Writer.TryWrite(wrapped))
                {
                    await wrapped.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is QuicException or AuthenticationException or ObjectDisposedException or OperationCanceledException)
        {
            error = exception;
        }
        finally
        {
            lock (_stateLock)
            {
                if (_state == ConnectionState.Open)
                {
                    _state = ConnectionState.Aborted;
                }
            }

            _inbound.Writer.TryComplete(error);
            CancelConnectionClosedToken();
            ReportClosed();
        }
    }

    private async ValueTask<MultiplexedConnectionStream> WrapStreamAsync(QuicStream stream, bool isInbound)
    {
        QuicStreamConnection connection;
        Action? onReleaseStarted = isInbound && stream.Type == QuicStreamType.Bidirectional
            ? _onInboundBidirectionalStreamReleaseStarted
            : null;

        try
        {
            connection = new QuicStreamConnection(
                stream,
                Id,
                LocalEndPoint,
                RemoteEndPoint,
                _streamOptions,
                _defaultStreamErrorCode,
                OnStreamDisposed,
                onReleaseStarted);
        }
        catch
        {
            // The stream options are connection-owned and shared by every stream, so a wrap
            // failure only releases the stream itself. It can release inbound stream credit too.
            onReleaseStarted?.Invoke();
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

    private void OnInboundBidirectionalStreamReleaseStarted()
    {
        // .NET requests DELAY_ID_FC_UPDATES: native stream credit is released when its handle closes.
        // Account before disposal can close it, so a concurrently received stream identifier is never
        // rejected against stale credit. Until disposal finishes, the bound can overstate that credit.
        long observed = Volatile.Read(ref _inboundBidirectionalStreamLimit);
        while (observed < maxStreamCount)
        {
            long previous = Interlocked.CompareExchange(ref _inboundBidirectionalStreamLimit, observed + 1, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    // Starts stream disposals without waiting for any, so their waits share one timeout. Before the
    // connection closes only bidirectional streams may end; afterward every remaining stream is released.
    private Task? DisposeStreams(bool bidirectionalOnly)
    {
        List<Task>? disposals = null;

        foreach (QuicStreamConnection stream in _streams.Values)
        {
            if (!bidirectionalOnly || stream.Direction == ConnectionDirection.Bidirectional)
            {
                (disposals ??= []).Add(stream.DisposeAsync().AsTask());
            }
        }

        return disposals is null ? null : Task.WhenAll(disposals);
    }

    // Waits for a group of stream disposals for at most CloseDeliveryTimeout. Running out of time is
    // not a failure: native connection teardown supersedes any stream shutdown still in progress.
    private async Task WaitForDeliveryAsync(Task deliveries)
    {
        if (deliveries.IsCompleted)
        {
            await deliveries.ConfigureAwait(false);
            return;
        }

        try
        {
            await deliveries.WaitAsync(_closeDeliveryTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The peer has not taken delivery in time; the connection close supersedes the rest.
        }
    }

    // The first abort or disposal decides the close, even while disposal waits for stream delivery.
    // Retain its code so disposal cannot overtake an abort between choosing the code and sending it.
    private void AbortCore(long errorCode)
    {
        long closeErrorCode;
        bool firstAbort;
        lock (_stateLock)
        {
            firstAbort = _state is not (ConnectionState.Aborted or ConnectionState.Closed);
            if (firstAbort && !_isDisposed)
            {
                _closeErrorCode = errorCode;
            }

            closeErrorCode = _closeErrorCode;
            if (firstAbort)
            {
                _state = ConnectionState.Aborted;
            }
        }

        // Abort is synchronous while closing a QUIC connection is asynchronous; fire and forget
        // the close, which observes its own faults so nothing surfaces as unobserved. An abort during
        // disposal ends its delivery wait immediately while retaining the code disposal selected.
        if (firstAbort)
        {
            _ = CloseConnectionAsync(closeErrorCode);
        }

        CancelConnectionClosedToken();
        ReportClosed();
    }

    private async Task CloseConnectionAsync(long errorCode)
    {
        try
        {
            await _connection.CloseAsync(errorCode).ConfigureAwait(false);
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

    // Abort and DisposeAsync both end the connection; whichever comes first reports it, once.
    private void ReportClosed()
    {
        if (Interlocked.Exchange(ref _closeReported, 1) == 0)
        {
            QuicConnectionEventSource.Log.ConnectionClosed(Id);
        }
    }

    private sealed class DebugView
    {
        private readonly QuicMultiplexedConnection _target;

        public DebugView(QuicMultiplexedConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public SslApplicationProtocol ApplicationProtocol => _target.ApplicationProtocol;
        public SslProtocols TlsProtocol => _target.TlsProtocol;
        public TlsCipherSuite CipherSuite => _target.CipherSuite;
        public X509Certificate2? RemoteCertificate => _target.RemoteCertificate;
        public ConnectionState State => _target.State;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
        public global::Assimalign.Cohesion.Connections.IConnectionFeatureCollection? InnerFeatures => _target.InnerFeatures;
    }
}
