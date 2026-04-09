namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Defines AMQP receiver settlement modes.
/// </summary>
public enum AmqpReceiverSettleMode : byte
{
    /// <summary>
    /// The receiver settles on first disposition.
    /// </summary>
    First = 0,

    /// <summary>
    /// The receiver settles on second disposition.
    /// </summary>
    Second = 1
}
