namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Defines what state of a terminus is retained durably (AMQP 1.0 Part 3, section 3.5.5).
/// </summary>
public enum AmqpTerminusDurability : uint
{
    /// <summary>
    /// No terminus state is retained durably.
    /// </summary>
    None = 0,

    /// <summary>
    /// Only the existence and configuration of the terminus is retained durably.
    /// </summary>
    Configuration = 1,

    /// <summary>
    /// The terminus configuration and the state of its unsettled deliveries are retained durably.
    /// </summary>
    UnsettledState = 2
}
