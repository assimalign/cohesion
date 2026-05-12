using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an AMQP error value.
/// </summary>
public sealed class AmqpError
{
    /// <summary>
    /// Gets or sets the error condition symbol.
    /// </summary>
    public AmqpSymbol Condition { get; set; }

    /// <summary>
    /// Gets or sets the human-readable error description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets additional error information keyed by symbol.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Info { get; set; }
}
