using System.Diagnostics;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a lifetime policy: when a node created on demand for a dynamic terminus is deleted (AMQP 1.0
/// Part 3, sections 3.5.10 to 3.5.13).
/// </summary>
/// <remarks>
/// <para>
/// A terminus that asks for a dynamic node names the policy it wants under
/// <see cref="AmqpNodeProperties.LifetimePolicy"/> in its dynamic-node-properties, and the endpoint that
/// creates the node reports the policy it applied the same way. Whatever the policy, a node lives at least
/// as long as the link that caused its creation.
/// </para>
/// <para>
/// The four policies AMQP 1.0 defines are the only instances, so they compare by reference. The codec
/// decodes each of them, under its numeric or its symbolic descriptor, to its instance here; a policy an
/// extension defines arrives as an <see cref="AmqpDescribedValue"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class AmqpLifetimePolicy
{
    private AmqpLifetimePolicy(string name)
    {
        Descriptor = new AmqpSymbol("amqp:" + name + ":list");
    }

    /// <summary>
    /// Gets the policy under which the node is deleted when the link that caused its creation ceases to
    /// exist (<c>delete-on-close</c>, section 3.5.10).
    /// </summary>
    public static AmqpLifetimePolicy DeleteOnClose { get; } = new("delete-on-close");

    /// <summary>
    /// Gets the policy under which the node is deleted when no link has it as its source or target
    /// (<c>delete-on-no-links</c>, section 3.5.11).
    /// </summary>
    public static AmqpLifetimePolicy DeleteOnNoLinks { get; } = new("delete-on-no-links");

    /// <summary>
    /// Gets the policy under which the node is deleted when the link that caused its creation no longer
    /// exists and the node holds no messages (<c>delete-on-no-messages</c>, section 3.5.12).
    /// </summary>
    public static AmqpLifetimePolicy DeleteOnNoMessages { get; } = new("delete-on-no-messages");

    /// <summary>
    /// Gets the policy under which the node is deleted when no link has it as its source or target and it
    /// holds no messages (<c>delete-on-no-links-or-messages</c>, section 3.5.13).
    /// </summary>
    public static AmqpLifetimePolicy DeleteOnNoLinksOrMessages { get; } = new("delete-on-no-links-or-messages");

    /// <summary>
    /// Gets the policy's symbolic descriptor, such as <c>amqp:delete-on-close:list</c>.
    /// </summary>
    public AmqpSymbol Descriptor { get; }

    /// <summary>
    /// Returns the policy's name.
    /// </summary>
    /// <returns>The policy's name, such as <c>delete-on-close</c>.</returns>
    public override string ToString()
    {
        // The descriptor is the name between the amqp: prefix and the :list suffix.
        return Descriptor.Value![5..^5];
    }
}
