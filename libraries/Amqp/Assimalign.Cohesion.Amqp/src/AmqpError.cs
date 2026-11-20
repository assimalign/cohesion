using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents an AMQP 1.0 error: the condition, description and information a peer attaches to a
/// <c>close</c>, <c>end</c>, <c>detach</c> or <c>rejected</c> outcome (Part 2, section 2.8.14).
/// </summary>
/// <remarks>
/// <see cref="AmqpErrorCondition"/> lists the conditions AMQP 1.0 defines. A peer may send any other
/// symbol; the condition is never restricted to that list.
/// </remarks>
public sealed class AmqpError
{
    /// <summary>
    /// Initializes a new AMQP error with no condition, for object-initializer use.
    /// </summary>
    public AmqpError()
    {
    }

    /// <summary>
    /// Initializes a new AMQP error.
    /// </summary>
    /// <param name="condition">The error condition.</param>
    /// <param name="description">A human-readable description of the error, or <see langword="null"/>.</param>
    /// <param name="info">Additional information about the error, or <see langword="null"/>.</param>
    public AmqpError(AmqpSymbol condition, string? description = null, IReadOnlyDictionary<AmqpSymbol, object?>? info = null)
    {
        Condition = condition;
        Description = description;
        Info = info;
    }

    /// <summary>
    /// Gets or sets the error condition. An error must carry one before it is encoded.
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

    /// <summary>
    /// Returns the condition, followed by the description when there is one.
    /// </summary>
    /// <returns>The error's condition and description.</returns>
    public override string ToString()
    {
        return Description is null ? Condition.ToString() : $"{Condition}: {Description}";
    }
}
