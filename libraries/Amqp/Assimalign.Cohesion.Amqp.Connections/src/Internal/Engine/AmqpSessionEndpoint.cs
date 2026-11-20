using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// A session on an AMQP connection (AMQP 1.0 Part 2, section 2.5): it pairs a local and a remote channel,
/// numbers the transfers of its links, applies both transfer windows, and routes link frames by handle.
/// </summary>
/// <remarks>
/// <para>
/// Members whose names end in <c>Locked</c> run under the connection's lock. A session the application
/// begins waits for the peer's answering <c>begin</c>; a session the peer begins is answered at once. It
/// stops carrying links as soon as either peer sends <c>end</c>, and it has ended once both have.
/// </para>
/// <para>
/// Unsettled deliveries are tracked here, by delivery-id, because a <c>disposition</c> names a range of
/// deliveries on the session rather than a link.
/// </para>
/// </remarks>
[DebuggerDisplay("AmqpSession {Channel} ({State})")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpSessionEndpoint : IAmqpSession
{
    // This endpoint never limits its transfers by an outgoing window. It declares the largest positive
    // 32-bit value, because some peers read the field as signed.
    private const uint outgoingWindow = int.MaxValue;

    // The transfer-id of the first transfer the session sends.
    private const uint initialOutgoingId = 1;

    private readonly AmqpConnectionEndpoint _connection;
    private readonly Lock _sync;
    private readonly AmqpFrameWriter _writer;
    private readonly ushort _channel;
    private readonly AmqpSessionSettings _settings;
    private readonly TaskCompletionSource<IAmqpSession> _begun;
    private readonly TaskCompletionSource _ended;
    private readonly CancellationTokenSource _endedSource;
    private readonly CancellationToken _endedToken;
    private readonly Channel<IAmqpLinkRequest> _requests;
    private readonly Dictionary<uint, AmqpLinkEndpoint> _localLinks;
    private readonly Dictionary<uint, AmqpLinkEndpoint> _remoteLinks;
    private readonly Dictionary<(string Name, AmqpRole Role), AmqpLinkEndpoint> _linksByName;
    private readonly Dictionary<uint, AmqpOutgoingDelivery> _outgoing;
    private readonly Dictionary<uint, AmqpIncomingDelivery> _incoming;
    private readonly List<uint> _matches;
    private readonly AmqpWaiter _window;
    private readonly AmqpFlowPerformative _flow;
    private readonly AmqpDispositionPerformative _disposition;
    private AmqpSessionState _state;
    private ushort? _remoteChannel;
    private AmqpException? _endedException;
    private bool _endWhenBegun;
    private uint _nextOutgoingId;
    private uint _remoteIncomingWindow;
    private uint _nextIncomingId;
    private uint _incomingWindow;
    private uint _remoteHandleMax;
    private uint _nextDeliveryId;
    private IReadOnlyList<AmqpSymbol>? _remoteOfferedCapabilities;
    private IReadOnlyList<AmqpSymbol>? _remoteDesiredCapabilities;
    private IReadOnlyDictionary<AmqpSymbol, object?>? _remoteProperties;

    public AmqpSessionEndpoint(AmqpConnectionEndpoint connection, ushort channel, AmqpSessionSettings settings)
    {
        _connection = connection;
        _sync = connection.Sync;
        _writer = connection.Writer;
        _channel = channel;
        _settings = settings;
        _begun = new TaskCompletionSource<IAmqpSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _endedSource = new CancellationTokenSource();
        _endedToken = _endedSource.Token;
        _requests = System.Threading.Channels.Channel.CreateUnbounded<IAmqpLinkRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        _localLinks = new Dictionary<uint, AmqpLinkEndpoint>();
        _remoteLinks = new Dictionary<uint, AmqpLinkEndpoint>();
        _linksByName = new Dictionary<(string Name, AmqpRole Role), AmqpLinkEndpoint>();
        _outgoing = new Dictionary<uint, AmqpOutgoingDelivery>();
        _incoming = new Dictionary<uint, AmqpIncomingDelivery>();
        _matches = new List<uint>();
        _window = new AmqpWaiter();
        _flow = new AmqpFlowPerformative();
        _disposition = new AmqpDispositionPerformative();
        _nextOutgoingId = initialOutgoingId;
        _incomingWindow = settings.IncomingWindow;
        _remoteHandleMax = uint.MaxValue;
        _state = AmqpSessionState.BeginSent;
    }

    public IAmqpConnection Connection => _connection;

    public ushort Channel => _channel;

    public IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities => _remoteOfferedCapabilities;

    public IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities => _remoteDesiredCapabilities;

    public IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties => _remoteProperties;

    public CancellationToken SessionEnded => _endedToken;

    /// <summary>
    /// Gets the lock that guards the connection and everything on it.
    /// </summary>
    internal Lock Sync => _sync;

    /// <summary>
    /// Gets the queue of frames the connection sends.
    /// </summary>
    internal AmqpFrameWriter Writer => _writer;

    /// <summary>
    /// Gets the largest frame, in bytes, the session may send.
    /// </summary>
    internal uint MaxFrameSize => _connection.MaxFrameSize;

    /// <summary>
    /// Gets how long an orderly end or detach waits for the peer's answer.
    /// </summary>
    internal TimeSpan CloseTimeout => _connection.CloseTimeout;

    /// <summary>
    /// Gets the peer's channel for the session, once its <c>begin</c> arrived.
    /// </summary>
    internal ushort? RemoteChannel => _remoteChannel;

    /// <summary>
    /// Gets the session's state, for diagnostics.
    /// </summary>
    internal AmqpSessionState State => _state;

    /// <summary>
    /// Gets a value indicating whether the session sent <c>begin</c> and waits for the peer's answer.
    /// </summary>
    internal bool IsAwaitingBegin => _state == AmqpSessionState.BeginSent && _remoteChannel is null;

    /// <summary>
    /// Gets a value indicating whether the session carries links: both peers began it and neither ended it.
    /// </summary>
    internal bool IsMappedLocked => _state == AmqpSessionState.Mapped;

    /// <summary>
    /// Gets a value indicating whether the peer's incoming window admits another transfer.
    /// </summary>
    internal bool HasOutgoingWindowLocked => _remoteIncomingWindow > 0;

    /// <summary>
    /// Gets the delivery-id the next delivery the session sends takes.
    /// </summary>
    internal uint NextDeliveryIdLocked => _nextDeliveryId;

    public async ValueTask<IAmqpSender> AttachSenderAsync(AmqpSenderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        AmqpSenderEndpoint sender;

        lock (_sync)
        {
            uint handle = ReserveLinkLocked(options.Name, AmqpRole.Sender, out string name);

            sender = new AmqpSenderEndpoint(this, handle, name, options);
            StartAttachLocked(sender);
        }

        await sender.WaitForAttachAsync(cancellationToken).ConfigureAwait(false);
        return sender;
    }

    public async ValueTask<IAmqpReceiver> AttachReceiverAsync(AmqpReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        AmqpReceiverEndpoint receiver;

        lock (_sync)
        {
            uint handle = ReserveLinkLocked(options.Name, AmqpRole.Receiver, out string name);

            receiver = new AmqpReceiverEndpoint(this, handle, name, options);
            StartAttachLocked(receiver);
        }

        await receiver.WaitForAttachAsync(cancellationToken).ConfigureAwait(false);
        return receiver;
    }

    public async ValueTask<IAmqpLinkRequest> AcceptLinkAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _requests.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public async ValueTask EndAsync(AmqpError? error = null, CancellationToken cancellationToken = default)
    {
        Task ended;

        lock (_sync)
        {
            if (_state == AmqpSessionState.BeginSent)
            {
                _endWhenBegun = true;
            }
            else
            {
                EndLocked(error);
                _writer.Schedule();
            }

            ended = _ended.Task;
        }

        try
        {
            await ended.WaitAsync(CloseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The peer never answered. The session already stopped carrying links; its channel is released
            // when the peer answers or the connection closes.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The token ends the wait only.
        }
    }

    public ValueTask DisposeAsync()
    {
        return EndAsync();
    }

    /// <summary>
    /// Sends the <c>begin</c> of a session the application begins.
    /// </summary>
    /// <exception cref="AmqpException">Thrown when the encoded <c>begin</c> exceeds the peer's maximum frame size.</exception>
    internal void BeginLocked()
    {
        WriteBeginLocked(null);
        _state = AmqpSessionState.BeginSent;
    }

    /// <summary>
    /// Waits for the peer to answer the session's <c>begin</c>.
    /// </summary>
    internal async ValueTask<IAmqpSession> WaitForBeginAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _begun.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                // The caller gave up: end the session once the peer has answered, since it cannot be unsent.
                if (_state == AmqpSessionState.BeginSent)
                {
                    _endWhenBegun = true;
                }
                else
                {
                    EndLocked(null);
                    _writer.Schedule();
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Maps the session when the peer answers its <c>begin</c>.
    /// </summary>
    internal void OnBeginAnsweredLocked(ushort remoteChannel, AmqpBeginPerformative begin)
    {
        ApplyRemoteBeginLocked(remoteChannel, begin);
        _state = AmqpSessionState.Mapped;

        if (_endWhenBegun)
        {
            EndLocked(null);
        }

        _begun.TrySetResult(this);
    }

    /// <summary>
    /// Answers a <c>begin</c> the peer sent, which maps the session.
    /// </summary>
    internal void OnBeginReceivedLocked(ushort remoteChannel, AmqpBeginPerformative begin)
    {
        ApplyRemoteBeginLocked(remoteChannel, begin);
        WriteBeginLocked(remoteChannel);
        _state = AmqpSessionState.Mapped;
    }

    /// <summary>
    /// Dispatches a frame the peer sent on the session's channel.
    /// </summary>
    internal void OnFrameLocked(AmqpPerformative performative, ReadOnlySpan<byte> payload)
    {
        if (_state == AmqpSessionState.EndSent)
        {
            // After sending end, the session waits only for the peer's end.
            if (performative is AmqpEndPerformative end)
            {
                OnEndLocked(end);
            }

            return;
        }

        if (_state != AmqpSessionState.Mapped)
        {
            return;
        }

        switch (performative)
        {
            case AmqpTransferPerformative transfer:
                OnTransferLocked(transfer, payload);
                break;
            case AmqpDispositionPerformative disposition:
                OnDispositionLocked(disposition);
                break;
            case AmqpFlowPerformative flow:
                OnFlowLocked(flow);
                break;
            case AmqpAttachPerformative attach:
                OnAttachLocked(attach);
                break;
            case AmqpDetachPerformative detach:
                OnDetachLocked(detach);
                break;
            case AmqpEndPerformative end:
                OnEndLocked(end);
                break;
        }
    }

    /// <summary>
    /// Ends the session because its connection closed.
    /// </summary>
    internal void OnConnectionClosedLocked(AmqpException connectionException)
    {
        if (_state == AmqpSessionState.Ended)
        {
            return;
        }

        bool begun = !IsAwaitingBegin;

        TerminateLocked(AmqpException.SessionEnded(null, connectionException));

        if (!begun && _begun.TrySetException(connectionException))
        {
            // Mark the failure observed: a caller that stopped waiting never reads it.
            _ = _begun.Task.Exception;
        }
    }

    /// <summary>
    /// Writes a frame on the session's channel.
    /// </summary>
    /// <exception cref="AmqpException">Thrown when the encoded frame exceeds the peer's maximum frame size.</exception>
    internal void WriteFrameLocked(AmqpPerformative performative)
    {
        _connection.WriteFrameLocked(_channel, performative);
    }

    /// <summary>
    /// Writes a <c>flow</c> with the session's window state and, for a link, the link's flow state.
    /// </summary>
    internal void WriteFlowLocked(AmqpLinkEndpoint? link)
    {
        AmqpFlowPerformative flow = _flow;

        flow.NextIncomingId = _nextIncomingId;
        flow.IncomingWindow = _incomingWindow;
        flow.NextOutgoingId = _nextOutgoingId;
        flow.OutgoingWindow = outgoingWindow;
        flow.Handle = null;
        flow.DeliveryCount = null;
        flow.LinkCredit = null;
        flow.Available = null;
        flow.Drain = false;
        flow.Echo = false;
        flow.Properties = null;

        link?.FillFlowLocked(flow);
        _connection.WriteFrameLocked(_channel, flow);
    }

    /// <summary>
    /// Writes a <c>disposition</c> for a range of deliveries.
    /// </summary>
    /// <exception cref="AmqpException">Thrown when the encoded frame exceeds the peer's maximum frame size.</exception>
    internal void WriteDispositionLocked(AmqpRole role, uint first, uint last, bool settled, AmqpDeliveryState? state)
    {
        AmqpDispositionPerformative disposition = _disposition;

        disposition.Role = role;
        disposition.First = first;
        disposition.Last = last == first ? null : last;
        disposition.Settled = settled;
        disposition.State = state;
        disposition.Batchable = false;

        try
        {
            _connection.WriteFrameLocked(_channel, disposition);
        }
        finally
        {
            disposition.State = null;
        }
    }

    /// <summary>
    /// Accounts for a <c>transfer</c> a sender on the session wrote: it takes a transfer-id and a place in
    /// the peer's incoming window.
    /// </summary>
    internal void OnTransferWrittenLocked()
    {
        Debug.Assert(_remoteIncomingWindow > 0);

        _nextOutgoingId = unchecked(_nextOutgoingId + 1);
        _remoteIncomingWindow--;
    }

    /// <summary>
    /// Returns a task that completes when the peer's incoming window may have opened, or the session or one
    /// of its links stopped. The caller awaits it outside the lock and checks again.
    /// </summary>
    internal Task WaitForWindowLocked()
    {
        return _window.WaitAsync();
    }

    /// <summary>
    /// Takes the delivery-id for a delivery a sender on the session starts.
    /// </summary>
    internal uint AllocateDeliveryIdLocked()
    {
        uint deliveryId = _nextDeliveryId;

        _nextDeliveryId = unchecked(deliveryId + 1);
        return deliveryId;
    }

    /// <summary>
    /// Tracks a delivery a sender sent unsettled, until the receiver's outcome settles it.
    /// </summary>
    internal void AddOutgoingLocked(AmqpOutgoingDelivery delivery)
    {
        _outgoing.Add(delivery.DeliveryId, delivery);
    }

    /// <summary>
    /// Stops tracking a delivery a sender aborted.
    /// </summary>
    internal void RemoveOutgoingLocked(uint deliveryId)
    {
        _outgoing.Remove(deliveryId);
    }

    /// <summary>
    /// Gets a value indicating whether an unsettled delivery the peer sent holds the delivery-id.
    /// </summary>
    internal bool ContainsIncomingLocked(uint deliveryId)
    {
        return _incoming.ContainsKey(deliveryId);
    }

    /// <summary>
    /// Tracks a delivery the peer sent unsettled, until settlement completes.
    /// </summary>
    internal void AddIncomingLocked(AmqpIncomingDelivery delivery)
    {
        _incoming.Add(delivery.DeliveryId, delivery);
    }

    /// <summary>
    /// Stops tracking a delivery the peer sent, because it settled or was aborted.
    /// </summary>
    internal void RemoveIncomingLocked(uint deliveryId)
    {
        _incoming.Remove(deliveryId);
    }

    /// <summary>
    /// Fails the deliveries of a link that stopped and wakes the senders waiting for the window, so each
    /// sees that its link stopped.
    /// </summary>
    internal void OnLinkStoppedLocked(AmqpLinkEndpoint link, AmqpException exception)
    {
        if (link.Role == AmqpRole.Sender)
        {
            FailOutgoingLocked(link, exception);
        }
        else
        {
            FailIncomingLocked(link, exception);
        }

        _window.Signal();
    }

    /// <summary>
    /// Releases a link's handles and name once both peers have detached it.
    /// </summary>
    internal void OnLinkDetachedLocked(AmqpLinkEndpoint link)
    {
        if (_localLinks.TryGetValue(link.Handle, out AmqpLinkEndpoint? local) && ReferenceEquals(local, link))
        {
            _localLinks.Remove(link.Handle);
        }

        if (link.RemoteHandle is uint remote
            && _remoteLinks.TryGetValue(remote, out AmqpLinkEndpoint? mapped)
            && ReferenceEquals(mapped, link))
        {
            _remoteLinks.Remove(remote);
        }

        if (_linksByName.TryGetValue((link.Name, link.Role), out AmqpLinkEndpoint? named) && ReferenceEquals(named, link))
        {
            _linksByName.Remove((link.Name, link.Role));
        }
    }

    /// <summary>
    /// Ends the session with an error, because the peer broke the protocol at the session's level.
    /// </summary>
    internal void EndWithErrorLocked(AmqpSymbol condition, string description)
    {
        EndLocked(new AmqpError(condition, description));
    }

    /// <summary>
    /// Gets the exception operations on the session fail with once it no longer carries links.
    /// </summary>
    internal AmqpException GetEndedExceptionLocked()
    {
        return _endedException ?? AmqpException.SessionEnded(null);
    }

    /// <summary>
    /// Throws the reason the session ended when it no longer carries links.
    /// </summary>
    internal void ThrowIfNotMappedLocked()
    {
        if (_state != AmqpSessionState.Mapped)
        {
            throw GetEndedExceptionLocked();
        }
    }

    private static string CreateLinkName(AmqpRole role)
    {
        return string.Concat(role == AmqpRole.Sender ? "sender-" : "receiver-", Guid.NewGuid().ToString("N"));
    }

    private uint ReserveLinkLocked(string? requestedName, AmqpRole role, out string name)
    {
        ThrowIfNotMappedLocked();

        name = requestedName ?? CreateLinkName(role);

        if (_linksByName.ContainsKey((name, role)))
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.LinkNameInUse, name));
        }

        if (!TryAllocateHandleLocked(out uint handle))
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandlesExhausted, _remoteHandleMax));
        }

        return handle;
    }

    private void StartAttachLocked(AmqpLinkEndpoint link)
    {
        // The attach is encoded first: one too large for the peer's frame size leaves nothing to undo.
        link.AttachLocked();

        _localLinks.Add(link.Handle, link);
        _linksByName.Add((link.Name, link.Role), link);
        _writer.Schedule();
    }

    private bool TryAllocateHandleLocked(out uint handle)
    {
        // The peer accepts handles up to the maximum its begin declared (AMQP 1.0 Part 2, section 2.7.2).
        for (uint candidate = 0; ; candidate++)
        {
            if (!_localLinks.ContainsKey(candidate))
            {
                handle = candidate;
                return true;
            }

            if (candidate == _remoteHandleMax)
            {
                handle = 0;
                return false;
            }
        }
    }

    private void ApplyRemoteBeginLocked(ushort remoteChannel, AmqpBeginPerformative begin)
    {
        _remoteChannel = remoteChannel;
        _nextIncomingId = begin.NextOutgoingId;
        _remoteIncomingWindow = begin.IncomingWindow;
        _remoteHandleMax = begin.HandleMax;
        _remoteOfferedCapabilities = begin.OfferedCapabilities;
        _remoteDesiredCapabilities = begin.DesiredCapabilities;
        _remoteProperties = begin.Properties;
    }

    private void WriteBeginLocked(ushort? remoteChannel)
    {
        _connection.WriteFrameLocked(_channel, new AmqpBeginPerformative
        {
            RemoteChannel = remoteChannel,
            NextOutgoingId = _nextOutgoingId,
            IncomingWindow = _incomingWindow,
            OutgoingWindow = outgoingWindow,
            HandleMax = _settings.HandleMax,
            OfferedCapabilities = _settings.OfferedCapabilities,
            DesiredCapabilities = _settings.DesiredCapabilities,
            Properties = _settings.Properties
        });
    }

    private void WriteEndLocked(AmqpError? error)
    {
        try
        {
            _connection.WriteFrameLocked(_channel, new AmqpEndPerformative { Error = error });
        }
        catch (AmqpException) when (error is not null)
        {
            // An error too large for a frame still ends the session, without its details.
            _connection.WriteFrameLocked(_channel, new AmqpEndPerformative { Error = new AmqpError(error.Condition) });
        }
    }

    /// <summary>
    /// Sends <c>end</c>: the session stops carrying links and waits for the peer's <c>end</c>.
    /// </summary>
    private void EndLocked(AmqpError? error)
    {
        if (_state != AmqpSessionState.Mapped)
        {
            return;
        }

        WriteEndLocked(error);
        _state = AmqpSessionState.EndSent;
        StopLocked(AmqpException.SessionEnded(error));
    }

    /// <summary>
    /// Stops carrying links: every link detaches with the reason, and everything waiting on the session fails.
    /// </summary>
    private void StopLocked(AmqpException exception)
    {
        _endedException ??= exception;

        // As on a link: the token is canceled before anything waiting fails, so a failure observed on
        // another thread always finds the session's token canceled.
        _ = _endedSource.CancelAsync();

        if (_localLinks.Count > 0)
        {
            AmqpLinkEndpoint[] links = new AmqpLinkEndpoint[_localLinks.Count];

            _localLinks.Values.CopyTo(links, 0);
            _localLinks.Clear();
            _remoteLinks.Clear();
            _linksByName.Clear();

            // Ending a session detaches its links without a detach frame (AMQP 1.0 Part 2, section 2.5.4).
            foreach (AmqpLinkEndpoint link in links)
            {
                link.OnSessionStoppedLocked(_endedException);
            }
        }

        _requests.Writer.TryComplete(_endedException);
        _window.Signal();
    }

    private void TerminateLocked(AmqpException exception)
    {
        _state = AmqpSessionState.Ended;
        StopLocked(exception);
        _connection.OnSessionEndedLocked(this);
        _ended.TrySetResult();
    }

    private void OnEndLocked(AmqpEndPerformative end)
    {
        if (_state == AmqpSessionState.Mapped)
        {
            // The peer ends the session: answer, then end.
            WriteEndLocked(null);
        }

        TerminateLocked(AmqpException.SessionEnded(end.Error));
    }

    private void OnAttachLocked(AmqpAttachPerformative attach)
    {
        // A peer that attaches beyond the declared maximum closes the connection (AMQP 1.0 Part 2, section 2.7.2).
        if (attach.Handle > _settings.HandleMax)
        {
            throw AmqpException.FramingError(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandleOutOfRange, attach.Handle, _settings.HandleMax));
        }

        if (_remoteLinks.ContainsKey(attach.Handle))
        {
            EndWithErrorLocked(
                AmqpErrorCondition.HandleInUse,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandleInUse, attach.Handle));
            return;
        }

        AmqpRole role = attach.Role == AmqpRole.Sender ? AmqpRole.Receiver : AmqpRole.Sender;

        if (_linksByName.TryGetValue((attach.Name, role), out AmqpLinkEndpoint? link))
        {
            if (link.State == AmqpLinkState.AttachSent)
            {
                // The peer answers a link this endpoint attached, or attached the same link at the same time.
                _remoteLinks.Add(attach.Handle, link);
                link.OnAttachAnsweredLocked(attach);
                return;
            }

            // Names identify links between the two containers, so a second link of the same name and
            // direction is refused (AMQP 1.0 Part 2, section 2.6.1).
            if (TryCreateRemoteLinkLocked(attach, role, out AmqpLinkEndpoint? duplicate))
            {
                duplicate.RefuseLocked(new AmqpError(
                    AmqpErrorCondition.NotAllowed,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.LinkNameInUse, attach.Name)));
            }

            return;
        }

        if (TryCreateRemoteLinkLocked(attach, role, out link))
        {
            // An attach whose fields contradict each other ends only its link (AMQP 1.0 Part 2, section 2.8.15).
            if (AmqpAttachRules.FindViolation(attach, opening: true) is string violation)
            {
                link.RefuseLocked(new AmqpError(AmqpErrorCondition.InvalidField, violation));
                return;
            }

            _linksByName.Add((attach.Name, role), link);
            _requests.Writer.TryWrite(new AmqpLinkRequest(this, link, attach));
        }
    }

    private bool TryCreateRemoteLinkLocked(AmqpAttachPerformative attach, AmqpRole role, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AmqpLinkEndpoint? link)
    {
        // Answering the attach takes a local handle, so a peer that attaches more links than its own handle
        // maximum lets this endpoint answer has exhausted the session.
        if (!TryAllocateHandleLocked(out uint handle))
        {
            EndWithErrorLocked(
                AmqpErrorCondition.ResourceLimitExceeded,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandlesExhausted, _remoteHandleMax));
            link = null;
            return false;
        }

        link = role == AmqpRole.Sender
            ? new AmqpSenderEndpoint(this, handle, attach)
            : new AmqpReceiverEndpoint(this, handle, attach);

        _localLinks.Add(handle, link);
        _remoteLinks.Add(attach.Handle, link);
        return true;
    }

    private void OnDetachLocked(AmqpDetachPerformative detach)
    {
        if (!_remoteLinks.Remove(detach.Handle, out AmqpLinkEndpoint? link))
        {
            EndWithErrorLocked(
                AmqpErrorCondition.UnattachedHandle,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandleUnattached, detach.Handle));
            return;
        }

        link.OnDetachLocked(detach);
    }

    private void OnFlowLocked(AmqpFlowPerformative flow)
    {
        // The peer's view of this endpoint's transfers sets how many more it may send (AMQP 1.0 Part 2,
        // section 2.5.6). A peer that has not seen this endpoint's begin counts from the initial transfer-id.
        uint nextIncomingId = flow.NextIncomingId ?? initialOutgoingId;
        int inFlight = AmqpSerialNumber.Difference(_nextOutgoingId, nextIncomingId);
        uint unseen = inFlight > 0 ? (uint)inFlight : 0;

        _remoteIncomingWindow = unseen >= flow.IncomingWindow ? 0 : flow.IncomingWindow - unseen;

        if (_remoteIncomingWindow > 0)
        {
            _window.Signal();
        }

        if (flow.Handle is uint handle)
        {
            if (!_remoteLinks.TryGetValue(handle, out AmqpLinkEndpoint? link))
            {
                EndWithErrorLocked(
                    AmqpErrorCondition.UnattachedHandle,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandleUnattached, handle));
                return;
            }

            link.OnFlowLocked(flow);
            return;
        }

        if (flow.Echo)
        {
            WriteFlowLocked(null);
        }
    }

    private void OnTransferLocked(AmqpTransferPerformative transfer, ReadOnlySpan<byte> payload)
    {
        if (!_remoteLinks.TryGetValue(transfer.Handle, out AmqpLinkEndpoint? link))
        {
            EndWithErrorLocked(
                AmqpErrorCondition.UnattachedHandle,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandleUnattached, transfer.Handle));
            return;
        }

        if (_incomingWindow == 0)
        {
            EndWithErrorLocked(AmqpErrorCondition.WindowViolation, ErrorMessages.WindowViolation);
            return;
        }

        _incomingWindow--;
        _nextIncomingId = unchecked(_nextIncomingId + 1);

        link.OnTransferLocked(transfer, payload);

        if (_state == AmqpSessionState.Mapped && _incomingWindow <= _settings.IncomingWindow / 2)
        {
            // Open the window again before the peer runs out of it.
            _incomingWindow = _settings.IncomingWindow;
            WriteFlowLocked(null);
        }
    }

    private void OnDispositionLocked(AmqpDispositionPerformative disposition)
    {
        uint first = disposition.First;
        uint last = disposition.Last ?? first;

        if (AmqpSerialNumber.Difference(last, first) < 0)
        {
            EndWithErrorLocked(
                AmqpErrorCondition.InvalidField,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.DispositionRangeInvalid, last, first));
            return;
        }

        // The role is the issuer's: a receiver's disposition names deliveries this endpoint sent.
        if (disposition.Role == AmqpRole.Receiver)
        {
            OnOutgoingDispositionLocked(first, last, disposition.Settled, disposition.State);
        }
        else
        {
            OnIncomingDispositionLocked(first, last, disposition.Settled, disposition.State);
        }
    }

    private void OnOutgoingDispositionLocked(uint first, uint last, bool settled, AmqpDeliveryState? state)
    {
        if (_outgoing.Count == 0)
        {
            return;
        }

        // An outcome, alone or inside a transactional state, is terminal: the sender may settle.
        bool terminal = state is AmqpOutcome or AmqpTransactionalState { Outcome: not null };
        List<uint> matches = CollectLocked(_outgoing, first, last);
        List<AmqpSenderEndpoint>? controlLinks = null;
        uint runFirst = 0;
        uint runLast = 0;
        bool inRun = false;

        foreach (uint deliveryId in matches)
        {
            AmqpOutgoingDelivery delivery = _outgoing[deliveryId];

            // The coordinator answers a declare or discharge with an outcome, never with transactional work
            // (AMQP 1.0 Part 4, section 4.1). The link fails once the range is walked, since failing it here
            // would change the deliveries under the walk.
            if (state is AmqpTransactionalState && delivery.Sender.IsControlLink)
            {
                (controlLinks ??= []).Add(delivery.Sender);
                continue;
            }

            delivery.RemoteState = state;

            if (!settled && !terminal)
            {
                continue;
            }

            _outgoing.Remove(deliveryId);
            delivery.Complete(state);

            if (settled)
            {
                continue;
            }

            // The receiver reached an outcome and waits for this endpoint to settle first (receiver settle
            // mode second): settle contiguous deliveries with one disposition each run.
            if (inRun && deliveryId == unchecked(runLast + 1))
            {
                runLast = deliveryId;
                continue;
            }

            if (inRun)
            {
                WriteDispositionLocked(AmqpRole.Sender, runFirst, runLast, true, null);
            }

            runFirst = deliveryId;
            runLast = deliveryId;
            inRun = true;
        }

        if (inRun)
        {
            WriteDispositionLocked(AmqpRole.Sender, runFirst, runLast, true, null);
        }

        matches.Clear();

        if (controlLinks is not null)
        {
            foreach (AmqpSenderEndpoint link in controlLinks)
            {
                if (link.State == AmqpLinkState.Attached)
                {
                    link.FailLocked(AmqpErrorCondition.NotAllowed, ErrorMessages.CoordinatorAnswerTransactional);
                }
            }
        }
    }

    private void OnIncomingDispositionLocked(uint first, uint last, bool settled, AmqpDeliveryState? state)
    {
        if (_incoming.Count == 0)
        {
            return;
        }

        List<uint> matches = CollectLocked(_incoming, first, last);
        List<(AmqpReceiverEndpoint Link, AmqpSymbol Condition, string Description)>? failures = null;

        foreach (uint deliveryId in matches)
        {
            AmqpIncomingDelivery delivery = _incoming[deliveryId];

            // A link that breaks a transaction rule fails once the range is walked, since failing it here would
            // change the deliveries under the walk. The control link carries no transactional work (AMQP 1.0
            // Part 4, section 4.1), and a delivery stays in the transaction its first frame named (section 4.4.1).
            if (state is AmqpTransactionalState && delivery.ReceiverEndpoint.IsControlLink)
            {
                (failures ??= []).Add((delivery.ReceiverEndpoint, AmqpErrorCondition.NotAllowed, ErrorMessages.CoordinatorDispositionTransactional));
                continue;
            }

            if (state is not null && !delivery.KeepsTransaction(state))
            {
                (failures ??= []).Add((
                    delivery.ReceiverEndpoint,
                    AmqpErrorCondition.InvalidField,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliveryTransactionChanged, deliveryId)));
                continue;
            }

            if (settled)
            {
                _incoming.Remove(deliveryId);
            }

            delivery.OnRemoteDispositionLocked(settled, state);
        }

        matches.Clear();

        if (failures is not null)
        {
            foreach ((AmqpReceiverEndpoint link, AmqpSymbol condition, string description) in failures)
            {
                // A link that broke two rules in one range fails once, with the first.
                if (link.State is AmqpLinkState.Attached or AmqpLinkState.RemoteAttached)
                {
                    link.FailLocked(condition, description);
                }
            }
        }
    }

    /// <summary>
    /// Collects the delivery-ids of a map that fall in a range, in serial order from the first.
    /// </summary>
    private List<uint> CollectLocked<T>(Dictionary<uint, T> map, uint first, uint last)
    {
        List<uint> matches = _matches;
        uint span = unchecked(last - first);

        if (span < (uint)map.Count)
        {
            for (uint offset = 0; ; offset++)
            {
                uint deliveryId = unchecked(first + offset);

                if (map.ContainsKey(deliveryId))
                {
                    matches.Add(deliveryId);
                }

                if (offset == span)
                {
                    break;
                }
            }

            return matches;
        }

        foreach (uint deliveryId in map.Keys)
        {
            if (AmqpSerialNumber.IsInRange(deliveryId, first, last))
            {
                matches.Add(deliveryId);
            }
        }

        if (matches.Count > 1)
        {
            matches.Sort((left, right) => unchecked(left - first).CompareTo(unchecked(right - first)));
        }

        return matches;
    }

    private void FailOutgoingLocked(AmqpLinkEndpoint link, AmqpException exception)
    {
        if (_outgoing.Count == 0)
        {
            return;
        }

        List<uint> matches = _matches;

        foreach (KeyValuePair<uint, AmqpOutgoingDelivery> pair in _outgoing)
        {
            if (ReferenceEquals(pair.Value.Sender, link))
            {
                matches.Add(pair.Key);
            }
        }

        foreach (uint deliveryId in matches)
        {
            if (_outgoing.Remove(deliveryId, out AmqpOutgoingDelivery? delivery))
            {
                delivery.Fail(exception);
            }
        }

        matches.Clear();
    }

    private void FailIncomingLocked(AmqpLinkEndpoint link, AmqpException exception)
    {
        if (_incoming.Count == 0)
        {
            return;
        }

        List<uint> matches = _matches;

        foreach (KeyValuePair<uint, AmqpIncomingDelivery> pair in _incoming)
        {
            if (ReferenceEquals(pair.Value.Receiver, link))
            {
                matches.Add(pair.Key);
            }
        }

        foreach (uint deliveryId in matches)
        {
            if (_incoming.Remove(deliveryId, out AmqpIncomingDelivery? delivery))
            {
                delivery.FailLocked(exception);
            }
        }

        matches.Clear();
    }

    private sealed class DebugView
    {
        private readonly AmqpSessionEndpoint _session;

        public DebugView(AmqpSessionEndpoint session)
        {
            _session = session;
        }

        public IAmqpConnection Connection => _session.Connection;
        public ushort Channel => _session.Channel;
        public bool IsEnded => _session.SessionEnded.IsCancellationRequested;
        public IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities => _session.RemoteOfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities => _session.RemoteDesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties => _session.RemoteProperties;
    }
}

/// <summary>
/// The states of a session endpoint.
/// </summary>
internal enum AmqpSessionState
{
    /// <summary>
    /// The session sent <c>begin</c> and waits for the peer's answer.
    /// </summary>
    BeginSent,

    /// <summary>
    /// Both peers began the session; it carries links.
    /// </summary>
    Mapped,

    /// <summary>
    /// The session sent <c>end</c> and waits for the peer's.
    /// </summary>
    EndSent,

    /// <summary>
    /// Both peers ended the session, or its connection closed.
    /// </summary>
    Ended
}
