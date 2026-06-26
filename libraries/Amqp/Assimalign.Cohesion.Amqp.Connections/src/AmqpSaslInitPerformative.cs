using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP SASL init performative.
/// </summary>
public sealed class AmqpSaslInitPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the selected SASL mechanism.
    /// </summary>
    public AmqpSymbol Mechanism { get; set; }

    /// <summary>
    /// Gets or sets the initial SASL response.
    /// </summary>
    public ReadOnlyMemory<byte>? InitialResponse { get; set; }

    /// <summary>
    /// Gets or sets the hostname associated with the SASL negotiation.
    /// </summary>
    public string? HostName { get; set; }
}
