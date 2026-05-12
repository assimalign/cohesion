using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents errors related to journal operations.
/// </summary>
public sealed class JournalException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="JournalException"/> with a message.
    /// </summary>
    /// <param name="message">Error message.</param>
    public JournalException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="JournalException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="innerException">Underlying cause.</param>
    public JournalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
