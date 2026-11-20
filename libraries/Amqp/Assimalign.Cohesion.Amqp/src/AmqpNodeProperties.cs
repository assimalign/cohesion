namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Provides the node properties AMQP 1.0 defines (Part 3, section 3.5.9): keys of the
/// dynamic-node-properties of a source or a target.
/// </summary>
/// <remarks>
/// A terminus that asks for a dynamic node gives the properties it wants, and the endpoint that creates the
/// node gives the properties the node actually has. Other properties, registered elsewhere or private to a
/// broker, sit beside these in the same map.
/// </remarks>
public static class AmqpNodeProperties
{
    /// <summary>
    /// Gets the key of the node's lifetime policy (<c>lifetime-policy</c>), whose value is an
    /// <see cref="AmqpLifetimePolicy"/>, or an <see cref="AmqpDescribedValue"/> for a policy an extension defines.
    /// </summary>
    public static AmqpSymbol LifetimePolicy { get; } = new("lifetime-policy");

    /// <summary>
    /// Gets the key of the distribution modes the node supports (<c>supported-dist-modes</c>), whose value is
    /// one <see cref="AmqpSymbol"/> or a non-empty array of them, such as <see cref="AmqpDistributionMode.Move"/>.
    /// </summary>
    public static AmqpSymbol SupportedDistributionModes { get; } = new("supported-dist-modes");
}
