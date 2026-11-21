using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the comparison of the unsettled maps a resumed link's two <c>attach</c> frames carry: which
/// deliveries from earlier attachments the sender resumes, which the receiver considers settled, and which the
/// receiver has no record of (AMQP 1.0 Part 2, section 2.6.13).
/// </summary>
/// <remarks>
/// <para>
/// The source's map is the sending endpoint's, and the target's map the receiving endpoint's. Every delivery
/// tag either map lists falls in exactly one of four sets:
/// </para>
/// <list type="table">
/// <listheader>
/// <term>Listed by</term>
/// <description>Set, and what each end does</description>
/// </listheader>
/// <item>
/// <term>Both maps</term>
/// <description>
/// <see cref="Resumable"/>: the sender MUST resume the delivery, or abort it when it cannot resume it to
/// completion (AMQP 1.0 Part 3, section 3.4.6).
/// </description>
/// </item>
/// <item>
/// <term>The target's only</term>
/// <description>
/// <see cref="Settled"/> when the source's map is complete: the receiver MUST consider the delivery settled,
/// and the sender ignores it. When the source's map is incomplete, the delivery is <see cref="Resumable"/>
/// instead: the sender resumes it if it still holds it, and MUST NOT otherwise.
/// </description>
/// </item>
/// <item>
/// <term>The source's only</term>
/// <description>
/// <see cref="UnknownToReceiver"/> when the target's map is complete: the receiver has no record of the
/// delivery and would ignore a resume, so the sender sends the message again as a new delivery, or settles it
/// when its own state shows the receiver had all of it. When the target's map is incomplete, the delivery is
/// <see cref="InDoubt"/> until a later attach.
/// </description>
/// </item>
/// </list>
/// <para>
/// New deliveries can be sent only once both maps are complete (<see cref="IsComplete"/>). Until then the two
/// ends resume and settle what they can, then detach and attach the link again.
/// </para>
/// </remarks>
[DebuggerDisplay("Resumable = {Resumable.Count}, Settled = {Settled.Count}, UnknownToReceiver = {UnknownToReceiver.Count}, InDoubt = {InDoubt.Count}")]
public sealed class AmqpUnsettledReconciliation
{
    private static readonly IReadOnlySet<AmqpDeliveryTag> _none = ReadOnlySet<AmqpDeliveryTag>.Empty;
    private static readonly AmqpUnsettledReconciliation _empty = new(_none, _none, _none, _none, false, false);

    private AmqpUnsettledReconciliation(
        IReadOnlySet<AmqpDeliveryTag> resumable,
        IReadOnlySet<AmqpDeliveryTag> settled,
        IReadOnlySet<AmqpDeliveryTag> unknownToReceiver,
        IReadOnlySet<AmqpDeliveryTag> inDoubt,
        bool sourceIncomplete,
        bool targetIncomplete)
    {
        Resumable = resumable;
        Settled = settled;
        UnknownToReceiver = unknownToReceiver;
        InDoubt = inDoubt;
        SourceIncomplete = sourceIncomplete;
        TargetIncomplete = targetIncomplete;
    }

    /// <summary>
    /// Gets the deliveries the sender resumes: the target's map lists them, and the source's map lists them too
    /// or is incomplete.
    /// </summary>
    /// <remarks>
    /// The receiver waits for each to be resumed or aborted, once, and refuses a new delivery under one of
    /// their tags until then.
    /// </remarks>
    public IReadOnlySet<AmqpDeliveryTag> Resumable { get; }

    /// <summary>
    /// Gets the deliveries only the target's map lists while the source's map is complete: the receiver MUST
    /// consider them settled, and the sender ignores them.
    /// </summary>
    public IReadOnlySet<AmqpDeliveryTag> Settled { get; }

    /// <summary>
    /// Gets the deliveries only the source's map lists while the target's map is complete: the receiver has no
    /// record of them, so it would ignore a resume.
    /// </summary>
    /// <remarks>
    /// To keep an at-least-once or exactly-once guarantee the sender sends such a message again, as a new
    /// delivery. A delivery the sender's own state shows the receiver had all of, such as one in a terminal
    /// state, needs nothing more (AMQP 1.0 Part 3, section 3.4.6).
    /// </remarks>
    public IReadOnlySet<AmqpDeliveryTag> UnknownToReceiver { get; }

    /// <summary>
    /// Gets the deliveries only the source's map lists while the target's map is incomplete: the receiver may
    /// hold them without having listed them, so they wait for a later attach.
    /// </summary>
    public IReadOnlySet<AmqpDeliveryTag> InDoubt { get; }

    /// <summary>
    /// Gets a value indicating whether the source's map was incomplete, so the absence of a tag from it is no
    /// evidence of settlement.
    /// </summary>
    public bool SourceIncomplete { get; }

    /// <summary>
    /// Gets a value indicating whether the target's map was incomplete, so the absence of a tag from it is no
    /// evidence of settlement.
    /// </summary>
    public bool TargetIncomplete { get; }

    /// <summary>
    /// Gets a value indicating whether both maps were complete, so the link can carry new deliveries as well as
    /// resumed ones.
    /// </summary>
    public bool IsComplete => !SourceIncomplete && !TargetIncomplete;

    /// <summary>
    /// Compares the unsettled maps of a link's two <c>attach</c> frames.
    /// </summary>
    /// <param name="sourceUnsettled">The sending endpoint's unsettled map, or <see langword="null"/> when it listed none.</param>
    /// <param name="sourceIncomplete">Whether the sending endpoint's map was incomplete.</param>
    /// <param name="targetUnsettled">The receiving endpoint's unsettled map, or <see langword="null"/> when it listed none.</param>
    /// <param name="targetIncomplete">Whether the receiving endpoint's map was incomplete.</param>
    /// <returns>The comparison.</returns>
    public static AmqpUnsettledReconciliation Create(
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? sourceUnsettled,
        bool sourceIncomplete,
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? targetUnsettled,
        bool targetIncomplete)
    {
        if (sourceUnsettled is not { Count: > 0 } && targetUnsettled is not { Count: > 0 } && !sourceIncomplete && !targetIncomplete)
        {
            return _empty;
        }

        HashSet<AmqpDeliveryTag>? resumable = null;
        HashSet<AmqpDeliveryTag>? settled = null;
        HashSet<AmqpDeliveryTag>? unknownToReceiver = null;
        HashSet<AmqpDeliveryTag>? inDoubt = null;

        if (targetUnsettled is not null)
        {
            foreach (AmqpDeliveryTag tag in targetUnsettled.Keys)
            {
                // The absence of a tag from an incomplete map is no evidence of settlement.
                if (sourceIncomplete || sourceUnsettled?.ContainsKey(tag) == true)
                {
                    (resumable ??= []).Add(tag);
                }
                else
                {
                    (settled ??= []).Add(tag);
                }
            }
        }

        if (sourceUnsettled is not null)
        {
            foreach (AmqpDeliveryTag tag in sourceUnsettled.Keys)
            {
                // A tag both maps list is resumable, and was counted with the target's map.
                if (targetUnsettled?.ContainsKey(tag) == true)
                {
                    continue;
                }

                if (targetIncomplete)
                {
                    (inDoubt ??= []).Add(tag);
                }
                else
                {
                    (unknownToReceiver ??= []).Add(tag);
                }
            }
        }

        return new AmqpUnsettledReconciliation(
            Wrap(resumable),
            Wrap(settled),
            Wrap(unknownToReceiver),
            Wrap(inDoubt),
            sourceIncomplete,
            targetIncomplete);
    }

    private static IReadOnlySet<AmqpDeliveryTag> Wrap(HashSet<AmqpDeliveryTag>? tags)
    {
        return tags is null ? _none : new ReadOnlySet<AmqpDeliveryTag>(tags);
    }
}
