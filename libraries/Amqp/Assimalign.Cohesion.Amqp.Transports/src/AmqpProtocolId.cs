namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Defines the AMQP protocol identifiers used in the eight-byte protocol header.
/// </summary>
public enum AmqpProtocolId : byte
{
    /// <summary>
    /// The core AMQP protocol.
    /// </summary>
    Amqp = 0,

    /// <summary>
    /// The TLS protocol layer.
    /// </summary>
    Tls = 2,

    /// <summary>
    /// The SASL protocol layer.
    /// </summary>
    Sasl = 3
}
