using System;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// Represents errors raised by the shared type system: unknown collations, malformed
/// key encodings, and unsupported type/encoding combinations.
/// </summary>
public sealed class DatabaseTypeException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="DatabaseTypeException"/> with a message.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    public DatabaseTypeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="DatabaseTypeException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    /// <param name="innerException">Underlying cause of the failure.</param>
    public DatabaseTypeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
