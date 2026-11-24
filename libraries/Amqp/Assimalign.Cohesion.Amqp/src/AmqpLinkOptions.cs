using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Provides the <c>attach</c> settings a sending and a receiving link share (AMQP 1.0 Part 2, section 2.7.3).
/// </summary>
/// <remarks>
/// <see cref="AmqpSenderOptions"/> and <see cref="AmqpReceiverOptions"/> add each role's settings. The link
/// reads the options once, when it attaches; changing them afterwards does not affect it.
/// </remarks>
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public abstract class AmqpLinkOptions
{
    private AmqpSenderSettleMode _senderSettleMode = AmqpSenderSettleMode.Mixed;
    private AmqpReceiverSettleMode _receiverSettleMode = AmqpReceiverSettleMode.First;
    private ulong? _maxMessageSize;

    private protected AmqpLinkOptions()
    {
    }

    /// <summary>
    /// Gets or sets the link name, which identifies the link between the two containers, or
    /// <see langword="null"/> to have the session generate a unique name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the source: the terminus the link's messages come from. The sending endpoint is
    /// authoritative for it, so a receiver proposes it and a sender decides it.
    /// </summary>
    public AmqpSource? Source { get; set; }

    /// <summary>
    /// Gets or sets the target: the terminus the link's messages go to. The receiving endpoint is
    /// authoritative for it, so a sender proposes it and a receiver decides it.
    /// </summary>
    public IAmqpTarget? Target { get; set; }

    /// <summary>
    /// Gets or sets the sender settlement mode. A sender uses it; a receiver proposes it. Defaults to
    /// <see cref="AmqpSenderSettleMode.Mixed"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is not a defined mode.</exception>
    public AmqpSenderSettleMode SenderSettleMode
    {
        get => _senderSettleMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _senderSettleMode = value;
        }
    }

    /// <summary>
    /// Gets or sets the receiver settlement mode. A receiver uses it; a sender proposes it. Defaults to
    /// <see cref="AmqpReceiverSettleMode.First"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is not a defined mode.</exception>
    public AmqpReceiverSettleMode ReceiverSettleMode
    {
        get => _receiverSettleMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _receiverSettleMode = value;
        }
    }

    /// <summary>
    /// Gets or sets the largest message, in bytes, the local endpoint accepts on the link, or
    /// <see langword="null"/> for no limit. A receiver detaches the link with
    /// <see cref="AmqpErrorCondition.MessageSizeExceeded"/> when a larger delivery arrives.
    /// </summary>
    /// <remarks>
    /// Defaults to 16 MiB (16,777,216 bytes) for <see cref="AmqpReceiverOptions"/>, which a link the peer attaches
    /// also takes when the application answers it without options, and to <see langword="null"/> for
    /// <see cref="AmqpSenderOptions"/>, whose value only tells the peer what the sender accepts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is zero; use <see langword="null"/> for no limit.</exception>
    public ulong? MaxMessageSize
    {
        get => _maxMessageSize;
        set
        {
            if (value is 0ul)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _maxMessageSize = value;
        }
    }

    /// <summary>
    /// Gets or sets the local endpoint's unsettled deliveries from an earlier attachment of the link, keyed
    /// by delivery tag, or <see langword="null"/> for none (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The link sends the map in its <c>attach</c>, so the peer can reconcile the deliveries a detach or a
    /// lost connection left unsettled. A value is the delivery's last known state, or
    /// <see langword="null"/> when the endpoint knows none.
    /// </para>
    /// <para>
    /// Once both <c>attach</c> frames are exchanged, the link compares the two maps into its
    /// <see cref="IAmqpLink.UnsettledReconciliation"/>, which decides what the sender resumes and what the
    /// receiver considers settled. The link keeps no state across attachments: the application keeps each
    /// delivery's state and data between them, and supplies the map again when it attaches the link under the
    /// same name.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? Unsettled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="Unsettled"/> holds only part of the unsettled
    /// deliveries, because there were too many to send in one <c>attach</c>.
    /// </summary>
    /// <remarks>
    /// While either end's map is incomplete the link carries only resumed deliveries: the two ends resume and
    /// settle what they can, then detach and attach the link again until both maps fit (AMQP 1.0 Part 2,
    /// section 2.6.13).
    /// </remarks>
    public bool IncompleteUnsettled { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities the local endpoint supports, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities the local endpoint can use if the peer supports them, or
    /// <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the link properties the local endpoint sends in its <c>attach</c>, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    private sealed class DebugView
    {
        private readonly AmqpLinkOptions _options;

        public DebugView(AmqpLinkOptions options)
        {
            _options = options;
        }

        public string? Name => _options.Name;
        public AmqpSource? Source => _options.Source;
        public IAmqpTarget? Target => _options.Target;
        public AmqpSenderSettleMode SenderSettleMode => _options.SenderSettleMode;
        public AmqpReceiverSettleMode ReceiverSettleMode => _options.ReceiverSettleMode;
        public ulong? MaxMessageSize => _options.MaxMessageSize;
        public IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? Unsettled => _options.Unsettled;
        public bool IncompleteUnsettled => _options.IncompleteUnsettled;
        public IReadOnlyList<AmqpSymbol>? OfferedCapabilities => _options.OfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? DesiredCapabilities => _options.DesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? Properties => _options.Properties;
    }
}
