using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// A link the peer attached, held until the application accepts or refuses it (AMQP 1.0 Part 2,
/// section 2.6.3).
/// </summary>
/// <remarks>
/// The session created the link endpoint when the peer's <c>attach</c> arrived, so its handle and name are
/// already reserved; answering only decides what the local <c>attach</c> carries. A request the peer
/// abandoned by detaching first, or whose session ended, fails to accept with that reason.
/// </remarks>
[DebuggerDisplay("AmqpLinkRequest {Name,nq} ({Role})")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpLinkRequest : IAmqpLinkRequest
{
    private readonly AmqpSessionEndpoint _session;
    private readonly AmqpLinkEndpoint _link;
    private readonly AmqpAttachPerformative _attach;
    private bool _answered;

    public AmqpLinkRequest(AmqpSessionEndpoint session, AmqpLinkEndpoint link, AmqpAttachPerformative attach)
    {
        _session = session;
        _link = link;
        _attach = attach;
    }

    public IAmqpSession Session => _session;

    public string Name => _attach.Name;

    public AmqpRole Role => _link.Role;

    public AmqpSource? Source => _attach.Source;

    public IAmqpTarget? Target => _attach.Target;

    public AmqpSenderSettleMode SenderSettleMode => _attach.SenderSettleMode;

    public AmqpReceiverSettleMode ReceiverSettleMode => _attach.ReceiverSettleMode;

    // A max-message-size of zero means the peer set no limit (AMQP 1.0 Part 2, section 2.7.3).
    public ulong? MaxMessageSize => _attach.MaxMessageSize is 0 ? null : _attach.MaxMessageSize;

    public IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? Unsettled => _attach.Unsettled;

    public bool IncompleteUnsettled => _attach.IncompleteUnsettled;

    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities => _attach.OfferedCapabilities;

    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities => _attach.DesiredCapabilities;

    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties => _attach.Properties;

    public ValueTask<IAmqpSender> AcceptSenderAsync(AmqpSenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_link is not AmqpSenderEndpoint sender)
        {
            throw new InvalidOperationException(ErrorMessages.LinkRequestNeedsReceiver);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IAmqpSender>(cancellationToken);
        }

        lock (_session.Sync)
        {
            ThrowIfAnsweredLocked();

            if (GetAnswerFailureLocked() is AmqpException failure)
            {
                return ValueTask.FromException<IAmqpSender>(failure);
            }

            try
            {
                // A failed answer, such as an attach larger than the peer's frames, leaves the request open.
                sender.AcceptLocked(options);
            }
            catch (AmqpException exception)
            {
                return ValueTask.FromException<IAmqpSender>(exception);
            }

            _answered = true;
            _session.Writer.Schedule();
        }

        return ValueTask.FromResult<IAmqpSender>(sender);
    }

    public ValueTask<IAmqpReceiver> AcceptReceiverAsync(AmqpReceiverOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_link is not AmqpReceiverEndpoint receiver)
        {
            throw new InvalidOperationException(ErrorMessages.LinkRequestNeedsSender);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IAmqpReceiver>(cancellationToken);
        }

        lock (_session.Sync)
        {
            ThrowIfAnsweredLocked();

            if (GetAnswerFailureLocked() is AmqpException failure)
            {
                return ValueTask.FromException<IAmqpReceiver>(failure);
            }

            try
            {
                receiver.AcceptLocked(options);
            }
            catch (AmqpException exception)
            {
                return ValueTask.FromException<IAmqpReceiver>(exception);
            }

            _answered = true;
            _session.Writer.Schedule();
        }

        return ValueTask.FromResult<IAmqpReceiver>(receiver);
    }

    public ValueTask RejectAsync(AmqpError error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        lock (_session.Sync)
        {
            ThrowIfAnsweredLocked();

            _answered = true;

            // A link the peer already detached, or whose session ended, needs no answer.
            if (GetAnswerFailureLocked() is null)
            {
                _link.RefuseLocked(error);
                _session.Writer.Schedule();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfAnsweredLocked()
    {
        // Answering twice is a usage error, so it throws at the call rather than through the returned task.
        if (_answered)
        {
            throw new InvalidOperationException(ErrorMessages.LinkRequestAnswered);
        }
    }

    private AmqpException? GetAnswerFailureLocked()
    {
        if (!_session.IsMappedLocked)
        {
            return _session.GetEndedExceptionLocked();
        }

        // The peer detached the link before the application answered it.
        return _link.State == AmqpLinkState.RemoteAttached ? null : _link.GetDetachedExceptionLocked();
    }

    private sealed class DebugView
    {
        private readonly AmqpLinkRequest _request;

        public DebugView(AmqpLinkRequest request)
        {
            _request = request;
        }

        public IAmqpSession Session => _request.Session;
        public string Name => _request.Name;
        public AmqpRole Role => _request.Role;
        public AmqpSource? Source => _request.Source;
        public IAmqpTarget? Target => _request.Target;
        public AmqpSenderSettleMode SenderSettleMode => _request.SenderSettleMode;
        public AmqpReceiverSettleMode ReceiverSettleMode => _request.ReceiverSettleMode;
        public ulong? MaxMessageSize => _request.MaxMessageSize;
        public IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? Unsettled => _request.Unsettled;
        public bool IncompleteUnsettled => _request.IncompleteUnsettled;
        public IReadOnlyList<AmqpSymbol>? OfferedCapabilities => _request.OfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? DesiredCapabilities => _request.DesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? Properties => _request.Properties;
    }
}
