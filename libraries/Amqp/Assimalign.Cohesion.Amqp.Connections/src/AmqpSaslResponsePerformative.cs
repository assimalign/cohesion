using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP SASL response performative.
/// </summary>
public sealed class AmqpSaslResponsePerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the response payload.
    /// </summary>
    public ReadOnlyMemory<byte> Response { get; set; }
}
