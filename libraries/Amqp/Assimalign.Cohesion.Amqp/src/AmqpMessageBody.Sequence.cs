using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a body of one or more <c>amqp-sequence</c> sections, each a list of AMQP values (AMQP 1.0
/// Part 3, section 3.2.7).
/// </summary>
public sealed class AmqpSequenceBody : AmqpMessageBody
{
    /// <summary>
    /// Initializes a body of one or more sequence sections.
    /// </summary>
    /// <param name="sequences">The sections, in order, each a list of AMQP values.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sequences"/> or one of its sections is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sequences"/> is empty.</exception>
    public AmqpSequenceBody(IReadOnlyList<IReadOnlyList<object?>> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);

        if (sequences.Count == 0)
        {
            throw new ArgumentException(ErrorMessages.BodySectionsEmpty, nameof(sequences));
        }

        for (int i = 0; i < sequences.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(sequences[i], nameof(sequences));
        }

        Sequences = sequences;
    }

    /// <summary>
    /// Gets the sequence sections, in order. There is at least one.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>> Sequences { get; }
}
