namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Provides the terminus expiry policies AMQP 1.0 defines (Part 3, section 3.5.6): the event that
/// starts a terminus's expiry timer.
/// </summary>
public static class AmqpTerminusExpiryPolicy
{
    /// <summary>
    /// Gets the policy under which the timer starts when the link is detached (<c>link-detach</c>).
    /// </summary>
    public static AmqpSymbol LinkDetach { get; } = new("link-detach");

    /// <summary>
    /// Gets the policy under which the timer starts when the session ends (<c>session-end</c>), the
    /// default when a terminus names none.
    /// </summary>
    public static AmqpSymbol SessionEnd { get; } = new("session-end");

    /// <summary>
    /// Gets the policy under which the timer starts when the connection closes (<c>connection-close</c>).
    /// </summary>
    public static AmqpSymbol ConnectionClose { get; } = new("connection-close");

    /// <summary>
    /// Gets the policy under which the terminus never expires (<c>never</c>).
    /// </summary>
    public static AmqpSymbol Never { get; } = new("never");
}
