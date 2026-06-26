using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP SASL outcome performative.
/// </summary>
public sealed class AmqpSaslOutcomePerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the SASL outcome code.
    /// </summary>
    public AmqpSaslCode Code { get; set; }

    /// <summary>
    /// Gets or sets the additional outcome data.
    /// </summary>
    public ReadOnlyMemory<byte>? AdditionalData { get; set; }
}
