using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a body of one or more <c>data</c> sections, each an opaque run of bytes (AMQP 1.0 Part 3,
/// section 3.2.6).
/// </summary>
public sealed class AmqpDataBody : AmqpMessageBody
{
    /// <summary>
    /// Initializes a body of one data section.
    /// </summary>
    /// <param name="data">The section's bytes.</param>
    public AmqpDataBody(ReadOnlyMemory<byte> data)
    {
        Sections = new[] { data };
    }

    /// <summary>
    /// Initializes a body of one or more data sections.
    /// </summary>
    /// <param name="sections">The sections, in order.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sections"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sections"/> is empty.</exception>
    public AmqpDataBody(IReadOnlyList<ReadOnlyMemory<byte>> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (sections.Count == 0)
        {
            throw new ArgumentException(ErrorMessages.BodySectionsEmpty, nameof(sections));
        }

        Sections = sections;
    }

    /// <summary>
    /// Gets the data sections, in order. There is at least one.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Sections { get; }
}
