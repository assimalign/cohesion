namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Defines the AMQP frame types carried after protocol negotiation.
/// </summary>
public enum AmqpFrameType : byte
{
    /// <summary>
    /// A standard AMQP performative frame.
    /// </summary>
    Amqp = 0,

    /// <summary>
    /// A SASL performative frame.
    /// </summary>
    Sasl = 1
}
