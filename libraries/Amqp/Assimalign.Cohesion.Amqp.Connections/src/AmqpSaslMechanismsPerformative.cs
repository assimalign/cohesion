using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP SASL mechanisms performative.
/// </summary>
public sealed class AmqpSaslMechanismsPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the supported SASL mechanisms.
    /// </summary>
    public IReadOnlyList<AmqpSymbol> SaslServerMechanisms { get; set; } = System.Array.Empty<AmqpSymbol>();
}
