using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// An open AMQP connection: the protocol engine that multiplexes sessions over one carrier connection
/// (AMQP 1.0 Part 2, section 2.4).
/// </summary>
/// <remarks>
/// <para>
/// One lock guards the connection and every session, link and delivery on it. A read loop decodes the
/// peer's frames and dispatches each under the lock; application calls take the same lock, update the
/// state and encode their frames into the frame writer's queue, which a write loop drains to the carrier.
/// Every completion the engine signals runs its continuations asynchronously, so no application code runs
/// under the lock.
/// </para>
/// <para>
/// The type derives from <see cref="DuplexConnection"/> only because the Connections layered listener
/// carries upgraded connections as <see cref="IDuplexConnection"/>. Its frames are not a byte stream, so
/// <see cref="Input"/> and <see cref="Output"/> throw.
/// </para>
/// </remarks>
[DebuggerDisplay("AmqpConnection {ContainerId,nq} -> {RemoteContainerId,nq} ({State})")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpConnectionEndpoint : DuplexConnection, IAmqpConnection
{
    private static readonly string _idleTimeoutExpiredFormat = ErrorMessages.IdleTimeoutExpired;

    private readonly IDuplexConnection _carrier;
    private readonly AmqpConnectionSettings _settings;
    private readonly Lock _sync;
    private readonly AmqpFrameWriter _writer;
    private readonly Channel<IAmqpSession> _accepted;
    private readonly Dictionary<ushort, AmqpSessionEndpoint> _localSessions;
    private readonly Dictionary<ushort, AmqpSessionEndpoint> _remoteSessions;
    private readonly CancellationTokenSource _closedSource;
    private readonly CancellationToken _closedToken;
    private readonly TaskCompletionSource _closeCompleted;
    private readonly TaskCompletionSource _released;
    private readonly IAmqpSaslConnectionInfo? _sasl;
    private readonly string _remoteContainerId;
    private readonly string? _remoteHostName;
    private readonly uint _maxFrameSize;
    private readonly ushort _channelMax;
    private readonly long _localIdleTimeout;
    private readonly long _remoteIdleTimeout;
    private readonly IReadOnlyList<AmqpSymbol>? _remoteOfferedCapabilities;
    private readonly IReadOnlyList<AmqpSymbol>? _remoteDesiredCapabilities;
    private readonly IReadOnlyDictionary<AmqpSymbol, object?>? _remoteProperties;
    private Timer? _idleTimer;
    private Timer? _closeTimer;
    private Task? _readLoop;
    private Task? _writeLoop;
    private ConnectionState _state;
    private AmqpException? _closedException;
    private long _lastReadTicks;
    private int _releasing;

    public AmqpConnectionEndpoint(
        IDuplexConnection carrier,
        AmqpConnectionSettings settings,
        AmqpOpenPerformative remoteOpen,
        IAmqpSaslConnectionInfo? sasl)
    {
        _carrier = carrier;
        _settings = settings;
        _sasl = sasl;
        _sync = new Lock();
        _remoteContainerId = remoteOpen.ContainerId;
        _remoteHostName = remoteOpen.HostName;

        // Frames the connection sends fit both limits; the peer's own limit is never below the minimum.
        _maxFrameSize = Math.Min(settings.MaxFrameSize, Math.Max(remoteOpen.MaxFrameSize, AmqpFrame.MinMaxFrameSize));
        _channelMax = Math.Min(settings.ChannelMax, remoteOpen.ChannelMax);
        _localIdleTimeout = settings.IdleTimeout > TimeSpan.Zero ? (long)settings.IdleTimeout.TotalMilliseconds : 0;
        _remoteIdleTimeout = remoteOpen.IdleTimeout ?? 0;
        _remoteOfferedCapabilities = remoteOpen.OfferedCapabilities;
        _remoteDesiredCapabilities = remoteOpen.DesiredCapabilities;
        _remoteProperties = remoteOpen.Properties;
        _writer = new AmqpFrameWriter(_sync, carrier.Output, _maxFrameSize);
        _accepted = Channel.CreateUnbounded<IAmqpSession>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        _localSessions = new Dictionary<ushort, AmqpSessionEndpoint>();
        _remoteSessions = new Dictionary<ushort, AmqpSessionEndpoint>();
        _closedSource = new CancellationTokenSource();
        _closedToken = _closedSource.Token;
        _closeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _state = ConnectionState.Opening;
    }

    public override ConnectionId Id => _carrier.Id;

    public override EndPoint? LocalEndPoint => _carrier.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _carrier.RemoteEndPoint;

    public override ConnectionCapabilities Capabilities => _carrier.Capabilities;

    public override ConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public override CancellationToken ConnectionClosed => _closedToken;

    public override PipeReader Input => throw new NotSupportedException(ErrorMessages.ConnectionNotByteStream);

    public override PipeWriter Output => throw new NotSupportedException(ErrorMessages.ConnectionNotByteStream);

    public string ContainerId => _settings.ContainerId;

    public string RemoteContainerId => _remoteContainerId;

    public string? RemoteHostName => _remoteHostName;

    public uint MaxFrameSize => _maxFrameSize;

    public ushort ChannelMax => _channelMax;

    public TimeSpan RemoteIdleTimeout => TimeSpan.FromMilliseconds(_remoteIdleTimeout);

    public IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities => _remoteOfferedCapabilities;

    public IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities => _remoteDesiredCapabilities;

    public IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties => _remoteProperties;

    /// <summary>
    /// Gets the lock that guards the connection and everything on it.
    /// </summary>
    internal Lock Sync => _sync;

    /// <summary>
    /// Gets the queue of frames the connection sends.
    /// </summary>
    internal AmqpFrameWriter Writer => _writer;

    /// <summary>
    /// Gets the settings of sessions begun without options.
    /// </summary>
    internal AmqpSessionSettings SessionSettings => _settings.SessionSettings;

    /// <summary>
    /// Gets how long closing the connection, ending a session or detaching a link waits for the peer's answer.
    /// </summary>
    internal TimeSpan CloseTimeout => _settings.CloseTimeout;

    protected override IConnectionFeatureCollection? InnerFeatures => _carrier.Features;

    protected override void ConfigureFeatures(IConnectionFeatureCollection features)
    {
        if (_sasl is not null)
        {
            features.Set(_sasl);
        }
    }

    /// <summary>
    /// Starts the read and write loops and the idle timer. Called once, when establishment completes.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            _state = ConnectionState.Open;
        }

        Volatile.Write(ref _lastReadTicks, Environment.TickCount64);

        // The loops outlive the call that establishes the connection, so they must not capture its
        // execution context: an ambient activity would otherwise parent every frame they process.
        bool restoreFlow = !ExecutionContext.IsFlowSuppressed();

        if (restoreFlow)
        {
            ExecutionContext.SuppressFlow();
        }

        try
        {
            _writeLoop = Task.Run(WriteLoopAsync);
            _readLoop = Task.Run(ReadLoopAsync);

            long period = GetTimerPeriod(_localIdleTimeout, _remoteIdleTimeout);

            if (period > 0)
            {
                _idleTimer = new Timer(
                    static state => ((AmqpConnectionEndpoint)state!).OnIdleTimer(),
                    this,
                    period,
                    period);
            }
        }
        finally
        {
            if (restoreFlow)
            {
                ExecutionContext.RestoreFlow();
            }
        }
    }

    public ValueTask<IAmqpSession> BeginSessionAsync(AmqpSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        AmqpSessionSettings settings = options is null ? _settings.SessionSettings : AmqpSessionSettings.From(options);
        AmqpSessionEndpoint session;

        lock (_sync)
        {
            // A connection the peer closed is not a usage error, so the failure is the returned task's.
            if (_state != ConnectionState.Open)
            {
                return ValueTask.FromException<IAmqpSession>(GetClosedExceptionLocked());
            }

            if (!TryAllocateChannelLocked(out ushort channel))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelsExhausted, _channelMax));
            }

            // The begin is written first: a begin too large for the peer's frames leaves no session behind.
            session = new AmqpSessionEndpoint(this, channel, settings);
            session.BeginLocked();
            _localSessions.Add(channel, session);
            _writer.Schedule();
        }

        return session.WaitForBeginAsync(cancellationToken);
    }

    public async ValueTask<IAmqpSession> AcceptSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public async ValueTask CloseAsync(AmqpError? error = null, CancellationToken cancellationToken = default)
    {
        Task closeCompleted;

        lock (_sync)
        {
            CloseLocked(error);
            closeCompleted = _closeCompleted.Task;
        }

        try
        {
            await closeCompleted.WaitAsync(_settings.CloseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The peer never answered: release the carrier anyway.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The token ends the wait only; the carrier is released either way.
        }

        await ReleaseAsync().ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        return CloseAsync();
    }

    public override void Abort(Exception? reason = null)
    {
        lock (_sync)
        {
            if (_state is not (ConnectionState.Closed or ConnectionState.Aborted))
            {
                TerminateLocked(
                    ConnectionState.Aborted,
                    AmqpException.ConnectionClosed(null, reason ?? new ConnectionAbortedException()));
                _writer.Abort();
            }
        }

        _carrier.Abort(reason);
        _ = ReleaseAsync();
    }

    /// <summary>
    /// Writes a frame on the connection, under the lock. A frame that fails leaves nothing written.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the performative carries a value the encoder refuses.</exception>
    /// <exception cref="AmqpException">Thrown when the encoded frame exceeds the peer's maximum frame size.</exception>
    internal void WriteFrameLocked(ushort channel, AmqpPerformative performative)
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        AmqpBufferWriter buffer = _writer.Buffer;
        int start = buffer.Length;

        try
        {
            AmqpFrame.Write(buffer, AmqpFrame.AmqpType, channel, performative);
        }
        catch (Exception)
        {
            // Justified catch-all: whatever the encoder throws, such as for a value the type system cannot
            // carry, the partial frame must not reach the carrier, where it would corrupt every frame behind it.
            buffer.Truncate(start);
            throw;
        }

        int size = buffer.Length - start;

        if (size > _maxFrameSize)
        {
            buffer.Truncate(start);

            throw new AmqpException(
                AmqpErrorCode.Other,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.FrameTooLarge, size, _maxFrameSize))
            {
                Error = new AmqpError(AmqpErrorCondition.FrameSizeTooSmall)
            };
        }
    }

    /// <summary>
    /// Releases a session's channels once both peers have ended it.
    /// </summary>
    internal void OnSessionEndedLocked(AmqpSessionEndpoint session)
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        if (_localSessions.TryGetValue(session.Channel, out AmqpSessionEndpoint? local) && ReferenceEquals(local, session))
        {
            _localSessions.Remove(session.Channel);
        }

        if (session.RemoteChannel is ushort remote
            && _remoteSessions.TryGetValue(remote, out AmqpSessionEndpoint? mapped)
            && ReferenceEquals(mapped, session))
        {
            _remoteSessions.Remove(remote);
        }
    }

    /// <summary>
    /// Gets the exception operations on the connection fail with once it is no longer open.
    /// </summary>
    internal AmqpException GetClosedExceptionLocked()
    {
        return _closedException ?? AmqpException.ConnectionClosed(null);
    }

    private bool TryAllocateChannelLocked(out ushort channel)
    {
        for (int candidate = 0; candidate <= _channelMax; candidate++)
        {
            if (!_localSessions.ContainsKey((ushort)candidate))
            {
                channel = (ushort)candidate;
                return true;
            }
        }

        channel = 0;
        return false;
    }

    /// <summary>
    /// Sends <c>close</c> and starts waiting for the peer's: the connection initiated the close, because
    /// the application asked, the peer broke the protocol or its idle timer expired.
    /// </summary>
    private void CloseLocked(AmqpError? error)
    {
        if (_state != ConnectionState.Open)
        {
            return;
        }

        try
        {
            WriteFrameLocked(0, new AmqpClosePerformative { Error = error });
        }
        catch (AmqpException)
        {
            // An error too large for a frame still closes the connection, without its details.
            WriteFrameLocked(0, new AmqpClosePerformative { Error = error is null ? null : new AmqpError(error.Condition) });
        }

        _state = ConnectionState.Closing;
        _closedException = AmqpException.ConnectionClosed(error);
        EndSessionsLocked(_closedException);
        _accepted.Writer.TryComplete(_closedException);
        _writer.Schedule();

        // Waits for the peer's close are bounded: a peer that never answers releases its carrier anyway.
        bool restoreFlow = !ExecutionContext.IsFlowSuppressed();

        if (restoreFlow)
        {
            ExecutionContext.SuppressFlow();
        }

        try
        {
            _closeTimer = new Timer(
                static state => _ = ((AmqpConnectionEndpoint)state!).ReleaseAsync(),
                this,
                _settings.CloseTimeout,
                Timeout.InfiniteTimeSpan);
        }
        finally
        {
            if (restoreFlow)
            {
                ExecutionContext.RestoreFlow();
            }
        }
    }

    /// <summary>
    /// Moves the connection to a terminal state and fails everything waiting on it.
    /// </summary>
    private void TerminateLocked(ConnectionState state, AmqpException exception)
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        _state = state;
        _closedException ??= exception;
        EndSessionsLocked(_closedException);
        _accepted.Writer.TryComplete(_closedException);
        _closeCompleted.TrySetResult();
        _ = _closedSource.CancelAsync();
    }

    private void EndSessionsLocked(AmqpException exception)
    {
        if (_localSessions.Count == 0)
        {
            _remoteSessions.Clear();
            return;
        }

        AmqpSessionEndpoint[] sessions = new AmqpSessionEndpoint[_localSessions.Count];

        _localSessions.Values.CopyTo(sessions, 0);
        _localSessions.Clear();
        _remoteSessions.Clear();

        foreach (AmqpSessionEndpoint session in sessions)
        {
            session.OnConnectionClosedLocked(exception);
        }
    }

    private async Task WriteLoopAsync()
    {
        Exception? failure = await _writer.RunAsync().ConfigureAwait(false);

        bool release;

        lock (_sync)
        {
            // The loop ends on its own only when the carrier stopped taking bytes: completing or aborting
            // the writer happens on the way to a terminal state.
            release = _state is ConnectionState.Open or ConnectionState.Closing;

            if (release)
            {
                TerminateLocked(
                    _state == ConnectionState.Closing ? ConnectionState.Closed : ConnectionState.Aborted,
                    AmqpException.ConnectionClosed(null, failure ?? new ConnectionResetException()));
            }
        }

        if (release)
        {
            _ = ReleaseAsync();
        }
    }

    private async Task ReadLoopAsync()
    {
        PipeReader input = _carrier.Input;
        byte[]? scratch = null;
        Exception? failure = null;
        bool carrierEnded = false;

        try
        {
            bool reading = true;

            while (reading)
            {
                ReadResult result = await input.ReadAsync().ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                try
                {
                    while (reading)
                    {
                        AmqpFrameHeader header;
                        ReadOnlySequence<byte> body;

                        try
                        {
                            if (!AmqpFrame.TryRead(ref buffer, _settings.MaxFrameSize, out header, out body))
                            {
                                break;
                            }
                        }
                        catch (AmqpException exception) when (exception.Error is not null)
                        {
                            // The bytes no longer form frames, so nothing after them can be read: report the
                            // violation and stop reading.
                            lock (_sync)
                            {
                                CloseLocked(exception.Error);
                            }

                            reading = false;
                            break;
                        }

                        Volatile.Write(ref _lastReadTicks, Environment.TickCount64);
                        reading = ProcessFrame(header, body, ref scratch);
                    }

                    if (reading && (result.IsCompleted || result.IsCanceled))
                    {
                        carrierEnded = true;
                        reading = false;
                    }
                }
                finally
                {
                    input.AdvanceTo(buffer.Start, buffer.End);
                }
            }

            await input.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Justified catch-all: the loop runs detached, so whatever ended the carrier's input is handed to
            // the connection, which owns the failure, instead of faulting an unobserved task.
            failure = exception;
            carrierEnded = true;
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        if (carrierEnded)
        {
            lock (_sync)
            {
                if (_state is ConnectionState.Open or ConnectionState.Closing)
                {
                    TerminateLocked(
                        _state == ConnectionState.Closing ? ConnectionState.Closed : ConnectionState.Aborted,
                        AmqpException.ConnectionClosed(null, failure ?? new ConnectionResetException()));
                    _writer.Abort();
                }
            }
        }

        // Nothing more can be read, so nothing more can happen: flush what is queued and release the carrier.
        _ = ReleaseAsync();
    }

    /// <summary>
    /// Decodes and dispatches one frame.
    /// </summary>
    /// <returns><see langword="true"/> to keep reading; <see langword="false"/> once the connection is closed.</returns>
    private bool ProcessFrame(AmqpFrameHeader header, ReadOnlySequence<byte> body, ref byte[]? scratch)
    {
        ReadOnlySpan<byte> span = default;
        AmqpPerformative? performative = null;
        AmqpError? violation = null;
        int payloadOffset = 0;

        if (header.Type != AmqpFrame.AmqpType)
        {
            // Once the connection is open only AMQP frames travel, empty or not (AMQP 1.0 Part 2, section 2.3.1).
            violation = new AmqpError(
                AmqpErrorCondition.FramingError,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.FrameTypeUnexpected, header.Type));
        }
        else if (body.IsEmpty)
        {
            if (header.Channel <= _settings.ChannelMax)
            {
                // An empty frame only keeps the connection alive, on any channel the peer may use (AMQP 1.0
                // Part 2, section 2.4.5).
                return true;
            }

            // A channel above the declared maximum is a framing error, empty frame or not (section 2.7.1).
            violation = new AmqpError(
                AmqpErrorCondition.FramingError,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelOutOfRange, header.Channel, _settings.ChannelMax));
        }
        else
        {
            if (body.IsSingleSegment)
            {
                span = body.FirstSpan;
            }
            else
            {
                int length = (int)body.Length;

                if (scratch is null || scratch.Length < length)
                {
                    if (scratch is not null)
                    {
                        ArrayPool<byte>.Shared.Return(scratch);
                    }

                    scratch = ArrayPool<byte>.Shared.Rent(length);
                }

                body.CopyTo(scratch);
                span = scratch.AsSpan(0, length);
            }

            try
            {
                AmqpReader reader = new(span);

                performative = AmqpPerformative.Decode(ref reader);
                payloadOffset = reader.Position;
            }
            catch (AmqpException exception) when (exception.Error is not null)
            {
                violation = exception.Error;
            }
        }

        lock (_sync)
        {
            if (_state is not (ConnectionState.Open or ConnectionState.Closing))
            {
                return false;
            }

            if (violation is not null)
            {
                // The frame boundaries still hold, so keep reading for the peer's close.
                CloseLocked(violation);
                return true;
            }

            if (_state == ConnectionState.Closing && performative is not AmqpClosePerformative)
            {
                // After sending close, an endpoint ignores everything but the peer's close.
                return true;
            }

            try
            {
                DispatchLocked(header.Channel, performative!, span.Slice(payloadOffset));
            }
            catch (AmqpException exception) when (exception.Error is not null)
            {
                // The peer broke the protocol at the connection's level, or a frame the protocol requires in
                // answer does not fit the peer's maximum frame size.
                CloseLocked(exception.Error);
            }

            _writer.Schedule();

            return _state is ConnectionState.Open or ConnectionState.Closing;
        }
    }

    private void DispatchLocked(ushort channel, AmqpPerformative performative, ReadOnlySpan<byte> payload)
    {
        // The peer may use channels up to the limit this endpoint declared, its close included (AMQP 1.0
        // Part 2, sections 2.4.3 and 2.7.1). Once this endpoint has sent close, only the peer's close gets
        // here, and it ends the connection whatever its channel: refusing it would only hold the carrier
        // until the close timeout.
        if (_state == ConnectionState.Open)
        {
            ThrowIfChannelOutOfRange(channel);
        }

        switch (performative)
        {
            case AmqpClosePerformative close:
                OnCloseLocked(close);
                return;
            case AmqpBeginPerformative begin:
                OnBeginLocked(channel, begin);
                return;
            case AmqpOpenPerformative:
            case AmqpSaslMechanismsPerformative:
            case AmqpSaslInitPerformative:
            case AmqpSaslChallengePerformative:
            case AmqpSaslResponsePerformative:
            case AmqpSaslOutcomePerformative:
                throw AmqpException.ProtocolViolation(
                    AmqpErrorCondition.IllegalState,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.PerformativeUnexpected, performative.PerformativeName));
        }

        if (!_remoteSessions.TryGetValue(channel, out AmqpSessionEndpoint? session))
        {
            throw AmqpException.ProtocolViolation(
                AmqpErrorCondition.IllegalState,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelUnmapped, channel));
        }

        session.OnFrameLocked(performative, payload);
    }

    private void ThrowIfChannelOutOfRange(ushort channel)
    {
        // The peer may use channels up to the limit this endpoint declared (AMQP 1.0 Part 2, section 2.7.1).
        if (channel > _settings.ChannelMax)
        {
            throw AmqpException.FramingError(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelOutOfRange, channel, _settings.ChannelMax));
        }
    }

    private void OnBeginLocked(ushort channel, AmqpBeginPerformative begin)
    {
        if (_remoteSessions.ContainsKey(channel))
        {
            throw AmqpException.ProtocolViolation(
                AmqpErrorCondition.IllegalState,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelInUse, channel));
        }

        AmqpSessionEndpoint? session;

        if (begin.RemoteChannel is ushort localChannel)
        {
            // The peer answers a begin this endpoint sent.
            if (!_localSessions.TryGetValue(localChannel, out session) || !session.IsAwaitingBegin)
            {
                throw AmqpException.ProtocolViolation(
                    AmqpErrorCondition.IllegalState,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.BeginUnsolicited, localChannel));
            }

            _remoteSessions.Add(channel, session);
            session.OnBeginAnsweredLocked(channel, begin);
            return;
        }

        // The peer begins a session: answer it at once with the configured session settings.
        if (!TryAllocateChannelLocked(out ushort allocated))
        {
            CloseLocked(new AmqpError(
                AmqpErrorCondition.ResourceLimitExceeded,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelsExhausted, _channelMax)));
            return;
        }

        session = new AmqpSessionEndpoint(this, allocated, _settings.SessionSettings);
        _localSessions.Add(allocated, session);
        _remoteSessions.Add(channel, session);
        session.OnBeginReceivedLocked(channel, begin);
        _accepted.Writer.TryWrite(session);
    }

    private void OnCloseLocked(AmqpClosePerformative close)
    {
        if (_state == ConnectionState.Open)
        {
            // The peer closes: answer with close, then end.
            WriteFrameLocked(0, new AmqpClosePerformative());
            TerminateLocked(ConnectionState.Closed, AmqpException.ConnectionClosed(close.Error));
        }
        else
        {
            // The peer answers the close this endpoint sent.
            TerminateLocked(ConnectionState.Closed, AmqpException.ConnectionClosed(close.Error));
        }

        _writer.Complete();
    }

    private void OnIdleTimer()
    {
        long now = Environment.TickCount64;

        lock (_sync)
        {
            if (_state != ConnectionState.Open)
            {
                return;
            }

            if (_localIdleTimeout > 0 && now - Volatile.Read(ref _lastReadTicks) > _localIdleTimeout)
            {
                // The peer went silent past the declared timeout: close it with the reason (Part 2, 2.4.5).
                CloseLocked(new AmqpError(
                    AmqpErrorCondition.ResourceLimitExceeded,
                    string.Format(CultureInfo.CurrentCulture, _idleTimeoutExpiredFormat, _settings.IdleTimeout)));
                return;
            }

            if (_remoteIdleTimeout > 0)
            {
                _writer.WriteHeartbeatIfIdle(now, GetHeartbeatInterval(_remoteIdleTimeout));
            }
        }
    }

    /// <summary>
    /// Gets how long, in milliseconds, this endpoint may write nothing before it sends an empty frame to
    /// keep the peer's idle timeout: half of it, so a frame reaches the peer well within it.
    /// </summary>
    /// <param name="remoteIdleTimeout">The idle timeout the peer declared in milliseconds.</param>
    internal static long GetHeartbeatInterval(long remoteIdleTimeout)
    {
        return remoteIdleTimeout / 2;
    }

    /// <summary>
    /// Gets the period, in milliseconds, of the idle timer, or zero when neither side has an idle timeout.
    /// </summary>
    /// <remarks>
    /// A quarter of each timeout bounds both checks. The timer sends an empty frame once nothing was written
    /// for half the peer's timeout, so the peer hears from this endpoint at least every three quarters of
    /// it; under 200 milliseconds the period floor takes over, and the gap is half the timeout plus
    /// <see cref="AmqpConnectionSettings.MinTimerPeriod"/>, still within it from
    /// <see cref="AmqpConnectionSettings.MinRemoteIdleTimeout"/> up. This endpoint notices a silent peer
    /// within a quarter of its own timeout, or one floor period.
    /// </remarks>
    /// <param name="localIdleTimeout">This endpoint's idle timeout in milliseconds, or zero for none.</param>
    /// <param name="remoteIdleTimeout">The idle timeout the peer declared in milliseconds, or zero for none.</param>
    internal static long GetTimerPeriod(long localIdleTimeout, long remoteIdleTimeout)
    {
        long period = 0;

        if (localIdleTimeout > 0)
        {
            period = localIdleTimeout / 4;
        }

        if (remoteIdleTimeout > 0)
        {
            long remote = remoteIdleTimeout / 4;

            period = period == 0 ? remote : Math.Min(period, remote);
        }

        return period == 0 ? 0 : Math.Max(period, AmqpConnectionSettings.MinTimerPeriod);
    }

    private Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _releasing, 1) != 0)
        {
            return _released.Task;
        }

        _ = ReleaseCoreAsync();
        return _released.Task;
    }

    private async Task ReleaseCoreAsync()
    {
        try
        {
            lock (_sync)
            {
                if (_state is not (ConnectionState.Closed or ConnectionState.Aborted))
                {
                    // A close the peer never answered, or a release before any close.
                    TerminateLocked(
                        _state == ConnectionState.Closing ? ConnectionState.Closed : ConnectionState.Aborted,
                        AmqpException.ConnectionClosed(null));
                }

                // Flush what is queued, such as this endpoint's close, before the carrier goes.
                _writer.Complete();
            }

            Task? writeLoop = _writeLoop;

            if (writeLoop is not null)
            {
                try
                {
                    await writeLoop.WaitAsync(_settings.CloseTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The carrier stopped draining: give up on the queued frames.
                    lock (_sync)
                    {
                        _writer.Abort();
                    }
                }
            }

            _idleTimer?.Dispose();
            _closeTimer?.Dispose();

            if (_readLoop is not null)
            {
                // The connection reads nothing more, and a carrier whose disposal leaves a pending read waiting
                // for the peer would hold the read loop open: end the read here.
                try
                {
                    _carrier.Input.CancelPendingRead();
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    // The read loop already completed the carrier's input.
                }
            }

            try
            {
                await _carrier.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Justified catch-all: the connection is already terminal and its owner learned why; a carrier
                // that fails to release itself cannot change that.
            }

            if (_readLoop is not null)
            {
                await _readLoop.ConfigureAwait(false);
            }

            if (writeLoop is not null)
            {
                await writeLoop.ConfigureAwait(false);
            }

            _writer.Dispose();
        }
        finally
        {
            _released.TrySetResult();
        }
    }

    private sealed class DebugView
    {
        private readonly AmqpConnectionEndpoint _connection;

        public DebugView(AmqpConnectionEndpoint connection)
        {
            _connection = connection;
        }

        public ConnectionId Id => _connection.Id;
        public EndPoint? LocalEndPoint => _connection.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;
        public ConnectionState State => _connection.State;
        public string ContainerId => _connection.ContainerId;
        public string RemoteContainerId => _connection.RemoteContainerId;
        public string? RemoteHostName => _connection.RemoteHostName;
        public uint MaxFrameSize => _connection.MaxFrameSize;
        public ushort ChannelMax => _connection.ChannelMax;
        public TimeSpan RemoteIdleTimeout => _connection.RemoteIdleTimeout;
        public IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities => _connection.RemoteOfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities => _connection.RemoteDesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties => _connection.RemoteProperties;
    }
}
