namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Defines AMQP SASL outcome codes.
/// </summary>
public enum AmqpSaslCode : byte
{
    /// <summary>
    /// SASL negotiation succeeded.
    /// </summary>
    Ok = 0,

    /// <summary>
    /// Authentication failed.
    /// </summary>
    Auth = 1,

    /// <summary>
    /// A permanent system failure occurred.
    /// </summary>
    Sys = 2,

    /// <summary>
    /// A permanent permission failure occurred.
    /// </summary>
    SysPerm = 3,

    /// <summary>
    /// A temporary system failure occurred.
    /// </summary>
    SysTemp = 4
}
