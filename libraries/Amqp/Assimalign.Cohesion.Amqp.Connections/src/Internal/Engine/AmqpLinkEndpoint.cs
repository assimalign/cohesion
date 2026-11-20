using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The lifecycle a sending and a receiving link endpoint share (AMQP 1.0 Part 2, section 2.6): attach,
/// refusal and detach, and the termini and settlement modes the two <c>attach</c> frames establish.
/// </summary>
/// <remarks>
/// <para>
/// Members whose names end in <c>Locked</c> run under the connection's lock. A link the application
/// attaches starts in <see cref="AmqpLinkState.AttachSent"/>; a link the peer attaches starts in
/// <see cref="AmqpLinkState.RemoteAttached"/> until the application accepts or refuses it.
/// </para>
/// <para>
/// A link stops carrying deliveries as soon as either peer sends <c>detach</c> or its session stops; its
/// handles and name are released once both peers have detached it.
/// </para>
/// </remarks>
[DebuggerDisplay("AmqpLink {Name,nq} ({Role}, {State})")]
[DebuggerTypeProxy(typeof(DebugView))]
internal abstract class AmqpLinkEndpoint : IAmqpLink
{
    private readonly AmqpSessionEndpoint _session;
    private readonly Lock _sync;
    private readonly string _name;
    private readonly uint _handle;
    private readonly AmqpRole _role;
    private readonly TaskCompletionSource _attached;
    private readonly TaskCompletionSource _detached;
    private readonly CancellationTokenSource _detachedSource;
    private readonly CancellationToken _detachedToken;
    private AmqpLinkState _state;
    private uint? _remoteHandle;
    private AmqpException? _detachedException;
    private bool _stopped;
    private bool _closeWhenAttached;
    private AmqpSource? _localSource;
    private IAmqpTarget? _localTarget;
    private AmqpSenderSettleMode _senderSettleMode;
    private AmqpReceiverSettleMode _receiverSettleMode;
    private ulong? _localMaxMessageSize;
    private IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? _localUnsettled;
    private bool _localIncompleteUnsettled;
    private IReadOnlyList<AmqpSymbol>? _offeredCapabilities;
    private IReadOnlyList<AmqpSymbol>? _desiredCapabilities;
    private IReadOnlyDictionary<AmqpSymbol, object?>? _properties;
    private AmqpSource? _remoteSource;
    private IAmqpTarget? _remoteTarget;
    private ulong? _remoteMaxMessageSize;
    private IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? _remoteUnsettled;
    private bool _remoteIncompleteUnsettled;
    private IReadOnlyList<AmqpSymbol>? _remoteOfferedCapabilities;
    private IReadOnlyList<AmqpSymbol>? _remoteDesiredCapabilities;
    private IReadOnlyDictionary<AmqpSymbol, object?>? _remoteProperties;

    /// <summary>
    /// Creates the endpoint of a link the application attaches.
    /// </summary>
    private protected AmqpLinkEndpoint(AmqpSessionEndpoint session, uint handle, string name, AmqpRole role, AmqpLinkOptions options)
        : this(session, handle, name, role)
    {
        _localSource = options.Source;
        _localTarget = options.Target;
        _senderSettleMode = options.SenderSettleMode;
        _receiverSettleMode = options.ReceiverSettleMode;
        _state = AmqpLinkState.AttachSent;
        ApplyLocalOptions(options);
    }

    /// <summary>
    /// Creates the endpoint of a link the peer attaches, which the application has yet to answer.
    /// </summary>
    private protected AmqpLinkEndpoint(AmqpSessionEndpoint session, uint handle, AmqpRole role, AmqpAttachPerformative attach)
        : this(session, handle, attach.Name, role)
    {
        // Until the application answers, the peer's proposal stands for both settlement modes.
        _remoteHandle = attach.Handle;
        _senderSettleMode = attach.SenderSettleMode;
        _receiverSettleMode = attach.ReceiverSettleMode;
        _state = AmqpLinkState.RemoteAttached;
        ApplyRemoteAttach(attach);
    }

    private AmqpLinkEndpoint(AmqpSessionEndpoint session, uint handle, string name, AmqpRole role)
    {
        _session = session;
        _sync = session.Sync;
        _handle = handle;
        _name = name;
        _role = role;
        _attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _detachedSource = new CancellationTokenSource();
        _detachedToken = _detachedSource.Token;
    }

    public IAmqpSession Session => _session;

    public string Name => _name;

    public AmqpRole Role => _role;

    public uint Handle => _handle;

    // The sender is authoritative for the source and the receiver for the target (AMQP 1.0 Part 2, section 2.6.3).
    public AmqpSource? Source => _role == AmqpRole.Sender ? _localSource : _remoteSource;

    public IAmqpTarget? Target => _role == AmqpRole.Sender ? _remoteTarget : _localTarget;

    public AmqpSenderSettleMode SenderSettleMode => _senderSettleMode;

    public AmqpReceiverSettleMode ReceiverSettleMode => _receiverSettleMode;

    public ulong? RemoteMaxMessageSize => _remoteMaxMessageSize;

    public IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? RemoteUnsettled => _remoteUnsettled;

    public bool RemoteIncompleteUnsettled => _remoteIncompleteUnsettled;

    public IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities => _remoteOfferedCapabilities;

    public IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities => _remoteDesiredCapabilities;

    public IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties => _remoteProperties;

    public CancellationToken LinkDetached => _detachedToken;

    /// <summary>
    /// Gets the link's state.
    /// </summary>
    internal AmqpLinkState State => _state;

    /// <summary>
    /// Gets the peer's handle for the link, once its <c>attach</c> arrived.
    /// </summary>
    internal uint? RemoteHandle => _remoteHandle;

    /// <summary>
    /// Gets the session the link is attached on.
    /// </summary>
    private protected AmqpSessionEndpoint SessionEndpoint => _session;

    /// <summary>
    /// Gets the lock that guards the connection and everything on it.
    /// </summary>
    private protected Lock Sync => _sync;

    /// <summary>
    /// Gets the largest message, in bytes, the local endpoint accepts, or <see langword="null"/> for no limit.
    /// </summary>
    private protected ulong? LocalMaxMessageSize => _localMaxMessageSize;

    /// <summary>
    /// Gets the delivery-count the link's <c>attach</c> announces: the sender's initial delivery-count, or
    /// <see langword="null"/> for a receiver.
    /// </summary>
    private protected abstract uint? InitialDeliveryCount { get; }

    public ValueTask DetachAsync(AmqpError? error = null, CancellationToken cancellationToken = default)
    {
        return DetachCoreAsync(false, error, cancellationToken);
    }

    public ValueTask CloseAsync(AmqpError? error = null, CancellationToken cancellationToken = default)
    {
        return DetachCoreAsync(true, error, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return CloseAsync();
    }

    /// <summary>
    /// Writes the link's part of a <c>flow</c>.
    /// </summary>
    internal abstract void FillFlowLocked(AmqpFlowPerformative flow);

    /// <summary>
    /// Applies a <c>flow</c> the peer sent for the link.
    /// </summary>
    internal abstract void OnFlowLocked(AmqpFlowPerformative flow);

    /// <summary>
    /// Applies a <c>transfer</c> the peer sent on the link.
    /// </summary>
    internal abstract void OnTransferLocked(AmqpTransferPerformative transfer, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Sends the <c>attach</c> of a link the application attaches.
    /// </summary>
    /// <exception cref="AmqpException">Thrown when the encoded <c>attach</c> exceeds the peer's maximum frame size.</exception>
    internal void AttachLocked()
    {
        _session.WriteFrameLocked(CreateAttach(_localSource, _localTarget));
        _state = AmqpLinkState.AttachSent;
        OnAttachSentLocked();
    }

    /// <summary>
    /// Waits for the peer to answer the link's <c>attach</c>.
    /// </summary>
    internal async ValueTask WaitForAttachAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _attached.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                // The attach cannot be unsent: close the link once the peer has answered it.
                if (_state == AmqpLinkState.AttachSent)
                {
                    _closeWhenAttached = true;
                }
                else
                {
                    DetachLocked(true, null);
                    _session.Writer.Schedule();
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Completes the attach of a link the application attached, when the peer answers it.
    /// </summary>
    internal void OnAttachAnsweredLocked(AmqpAttachPerformative attach)
    {
        _remoteHandle = attach.Handle;
        ApplyRemoteAttach(attach);

        // The sender decides the sender settlement mode and the receiver the receiver settlement mode, so the
        // peer's attach settles the mode its role owns (AMQP 1.0 Part 2, section 2.7.3).
        if (_role == AmqpRole.Sender)
        {
            _receiverSettleMode = attach.ReceiverSettleMode;
        }
        else
        {
            _senderSettleMode = attach.SenderSettleMode;
        }

        // A peer refuses a link by answering without the terminus it owns, then detaching it (section 2.6.3).
        if (_role == AmqpRole.Sender ? attach.Target is null : attach.Source is null)
        {
            _state = AmqpLinkState.AttachRefused;
            return;
        }

        _state = AmqpLinkState.Attached;
        OnRemoteAttachLocked(attach);

        if (_closeWhenAttached)
        {
            DetachLocked(true, null);
        }

        _attached.TrySetResult();
    }

    /// <summary>
    /// Refuses a link the peer attached: answers without the terminus this endpoint owns, then closes the
    /// link with the error (AMQP 1.0 Part 2, section 2.6.3).
    /// </summary>
    internal void RefuseLocked(AmqpError error)
    {
        Debug.Assert(_state == AmqpLinkState.RemoteAttached);

        WriteRefusalLocked();
        WriteDetachLocked(true, error);
        _state = AmqpLinkState.DetachSent;
        StopLocked(AmqpException.LinkDetached(error));
    }

    /// <summary>
    /// Detaches the link with an error, because the peer broke the protocol at the link's level.
    /// </summary>
    internal void FailLocked(AmqpSymbol condition, string description)
    {
        AmqpError error = new(condition, description);

        if (_state == AmqpLinkState.RemoteAttached)
        {
            RefuseLocked(error);
            return;
        }

        DetachLocked(true, error);
    }

    /// <summary>
    /// Applies the peer's <c>detach</c>. The session already released the peer's handle.
    /// </summary>
    internal void OnDetachLocked(AmqpDetachPerformative detach)
    {
        switch (_state)
        {
            case AmqpLinkState.RemoteAttached:
                // The peer gave up before the application answered: answer as a refusal, so both ends close.
                WriteRefusalLocked();
                WriteDetachLocked(detach.Closed, null);
                DetachedLocked(AmqpException.LinkDetached(detach.Error));
                break;
            case AmqpLinkState.AttachRefused:
                WriteDetachLocked(detach.Closed, null);
                DetachedLocked(AmqpException.LinkRefused(detach.Error));
                break;
            case AmqpLinkState.Attached:
                // The peer detaches: answer with the same closed flag (AMQP 1.0 Part 2, section 2.6.6).
                WriteDetachLocked(detach.Closed, null);
                DetachedLocked(AmqpException.LinkDetached(detach.Error));
                break;
            case AmqpLinkState.DetachSent:
                // The peer answers the detach this endpoint sent.
                DetachedLocked(_detachedException ?? AmqpException.LinkDetached(detach.Error));
                break;
        }
    }

    /// <summary>
    /// Detaches the link without a frame, because its session stopped (AMQP 1.0 Part 2, section 2.5.4).
    /// </summary>
    internal void OnSessionStoppedLocked(AmqpException sessionException)
    {
        if (_state == AmqpLinkState.Detached)
        {
            return;
        }

        _state = AmqpLinkState.Detached;
        StopLocked(AmqpException.LinkDetached(null, sessionException));

        // An attach still waiting for the peer's answer fails with the session's reason.
        if (_attached.TrySetException(sessionException))
        {
            _ = _attached.Task.Exception;
        }

        _detached.TrySetResult();
    }

    /// <summary>
    /// Answers the <c>attach</c> of a link the peer attached, accepting it with the application's options.
    /// </summary>
    /// <exception cref="AmqpException">Thrown when the encoded <c>attach</c> exceeds the peer's maximum frame size; the request stays unanswered.</exception>
    private protected void AnswerLocked(AmqpLinkOptions? options)
    {
        Debug.Assert(_state == AmqpLinkState.RemoteAttached);

        if (_role == AmqpRole.Sender)
        {
            // The sender decides the source and its settlement mode; the target stays the receiver's.
            _localSource = options?.Source ?? _remoteSource;
            _localTarget = _remoteTarget;

            if (options is not null)
            {
                _senderSettleMode = options.SenderSettleMode;
            }
        }
        else
        {
            // The receiver decides the target and its settlement mode; the source stays the sender's.
            _localSource = _remoteSource;
            _localTarget = options?.Target ?? _remoteTarget;

            if (options is not null)
            {
                _receiverSettleMode = options.ReceiverSettleMode;
            }
        }

        if (options is not null)
        {
            ApplyLocalOptions(options);
        }

        _session.WriteFrameLocked(CreateAttach(_localSource, _localTarget));
        _state = AmqpLinkState.Attached;
        _attached.TrySetResult();
        OnAcceptedLocked();
    }

    /// <summary>
    /// Sends <c>detach</c>: the link stops carrying deliveries and waits for the peer's <c>detach</c>.
    /// </summary>
    private protected void DetachLocked(bool closed, AmqpError? error)
    {
        if (_state != AmqpLinkState.Attached)
        {
            return;
        }

        WriteDetachLocked(closed, error);
        _state = AmqpLinkState.DetachSent;
        StopLocked(AmqpException.LinkDetached(error));
    }

    /// <summary>
    /// Throws the reason the link stopped when it no longer carries deliveries.
    /// </summary>
    private protected void ThrowIfNotAttachedLocked()
    {
        if (_state != AmqpLinkState.Attached)
        {
            throw GetDetachedExceptionLocked();
        }
    }

    /// <summary>
    /// Gets the exception operations on the link fail with once it no longer carries deliveries.
    /// </summary>
    internal AmqpException GetDetachedExceptionLocked()
    {
        return _detachedException ?? AmqpException.LinkDetached(null);
    }

    /// <summary>
    /// Called once the <c>attach</c> of a link the application attaches is written.
    /// </summary>
    private protected virtual void OnAttachSentLocked()
    {
    }

    /// <summary>
    /// Called once the peer accepted a link the application attached.
    /// </summary>
    private protected virtual void OnRemoteAttachLocked(AmqpAttachPerformative attach)
    {
    }

    /// <summary>
    /// Called once the application accepted a link the peer attached and the answer is written.
    /// </summary>
    private protected virtual void OnAcceptedLocked()
    {
    }

    /// <summary>
    /// Called once, when the link stops carrying deliveries, to fail what waits on it.
    /// </summary>
    private protected abstract void OnStoppedLocked(AmqpException exception);

    private async ValueTask DetachCoreAsync(bool closed, AmqpError? error, CancellationToken cancellationToken)
    {
        Task detached;

        lock (_sync)
        {
            DetachLocked(closed, error);
            _session.Writer.Schedule();
            detached = _detached.Task;
        }

        try
        {
            await detached.WaitAsync(_session.CloseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The peer never answered. The link already stopped; its handle is released when the peer
            // answers or the session ends.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The token ends the wait only.
        }
    }

    private void StopLocked(AmqpException exception)
    {
        _detachedException ??= exception;

        if (_stopped)
        {
            return;
        }

        _stopped = true;
        OnStoppedLocked(_detachedException);
        _session.OnLinkStoppedLocked(this, _detachedException);
        _ = _detachedSource.CancelAsync();
    }

    private void DetachedLocked(AmqpException exception)
    {
        _state = AmqpLinkState.Detached;
        StopLocked(exception);

        // A refused attach fails with the peer's reason.
        if (_attached.TrySetException(_detachedException!))
        {
            _ = _attached.Task.Exception;
        }

        _detached.TrySetResult();
        _session.OnLinkDetachedLocked(this);
    }

    private void WriteRefusalLocked()
    {
        // Both termini stay out: the one this endpoint owns marks the refusal, and echoing the other could
        // exceed a peer that declared a smaller frame size than the attach it sent.
        _session.WriteFrameLocked(CreateAttach(null, null));
    }

    private void WriteDetachLocked(bool closed, AmqpError? error)
    {
        try
        {
            _session.WriteFrameLocked(new AmqpDetachPerformative { Handle = _handle, Closed = closed, Error = error });
        }
        catch (AmqpException) when (error is not null)
        {
            // An error too large for a frame still detaches the link, without its details.
            _session.WriteFrameLocked(new AmqpDetachPerformative
            {
                Handle = _handle,
                Closed = closed,
                Error = new AmqpError(error.Condition)
            });
        }
    }

    private AmqpAttachPerformative CreateAttach(AmqpSource? source, IAmqpTarget? target)
    {
        return new AmqpAttachPerformative
        {
            Name = _name,
            Handle = _handle,
            Role = _role,
            SenderSettleMode = _senderSettleMode,
            ReceiverSettleMode = _receiverSettleMode,
            Source = source,
            Target = target,
            Unsettled = _localUnsettled,
            IncompleteUnsettled = _localIncompleteUnsettled,
            InitialDeliveryCount = InitialDeliveryCount,
            MaxMessageSize = _localMaxMessageSize,
            OfferedCapabilities = _offeredCapabilities,
            DesiredCapabilities = _desiredCapabilities,
            Properties = _properties
        };
    }

    private void ApplyLocalOptions(AmqpLinkOptions options)
    {
        _localMaxMessageSize = options.MaxMessageSize;
        _localUnsettled = options.Unsettled;
        _localIncompleteUnsettled = options.IncompleteUnsettled;
        _offeredCapabilities = options.OfferedCapabilities;
        _desiredCapabilities = options.DesiredCapabilities;
        _properties = options.Properties;
    }

    private void ApplyRemoteAttach(AmqpAttachPerformative attach)
    {
        _remoteSource = attach.Source;
        _remoteTarget = attach.Target;

        // Zero, like an absent value, sets no limit (AMQP 1.0 Part 2, section 2.7.3).
        _remoteMaxMessageSize = attach.MaxMessageSize is 0 ? null : attach.MaxMessageSize;
        _remoteUnsettled = attach.Unsettled;
        _remoteIncompleteUnsettled = attach.IncompleteUnsettled;
        _remoteOfferedCapabilities = attach.OfferedCapabilities;
        _remoteDesiredCapabilities = attach.DesiredCapabilities;
        _remoteProperties = attach.Properties;
    }

    private sealed class DebugView
    {
        private readonly AmqpLinkEndpoint _link;

        public DebugView(AmqpLinkEndpoint link)
        {
            _link = link;
        }

        public IAmqpSession Session => _link.Session;
        public string Name => _link.Name;
        public AmqpRole Role => _link.Role;
        public uint Handle => _link.Handle;
        public AmqpSource? Source => _link.Source;
        public IAmqpTarget? Target => _link.Target;
        public AmqpSenderSettleMode SenderSettleMode => _link.SenderSettleMode;
        public AmqpReceiverSettleMode ReceiverSettleMode => _link.ReceiverSettleMode;
        public ulong? RemoteMaxMessageSize => _link.RemoteMaxMessageSize;
        public IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? RemoteUnsettled => _link.RemoteUnsettled;
        public bool RemoteIncompleteUnsettled => _link.RemoteIncompleteUnsettled;
        public bool IsDetached => _link.LinkDetached.IsCancellationRequested;
    }
}

/// <summary>
/// The states of a link endpoint.
/// </summary>
internal enum AmqpLinkState
{
    /// <summary>
    /// The link sent <c>attach</c> and waits for the peer's answer.
    /// </summary>
    AttachSent,

    /// <summary>
    /// The peer answered without the terminus it owns, refusing the link, and its <c>detach</c> follows.
    /// </summary>
    AttachRefused,

    /// <summary>
    /// The peer sent <c>attach</c>, and the application has yet to accept or refuse it.
    /// </summary>
    RemoteAttached,

    /// <summary>
    /// Both peers attached the link; it carries deliveries.
    /// </summary>
    Attached,

    /// <summary>
    /// The link sent <c>detach</c> and waits for the peer's.
    /// </summary>
    DetachSent,

    /// <summary>
    /// Both peers detached the link, or its session stopped.
    /// </summary>
    Detached
}
