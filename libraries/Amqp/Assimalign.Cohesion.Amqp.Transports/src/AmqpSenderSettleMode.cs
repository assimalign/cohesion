namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Defines AMQP sender settlement modes.
/// </summary>
public enum AmqpSenderSettleMode : byte
{
    /// <summary>
    /// Deliveries are sent unsettled.
    /// </summary>
    Unsettled = 0,

    /// <summary>
    /// Deliveries are sent settled.
    /// </summary>
    Settled = 1,

    /// <summary>
    /// Deliveries may be sent either settled or unsettled.
    /// </summary>
    Mixed = 2
}
