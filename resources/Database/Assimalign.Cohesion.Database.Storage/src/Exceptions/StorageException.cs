using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Base exception type for errors raised by the storage layer.
/// </summary>
public abstract class StorageException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="StorageException"/> with a message.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    public StorageException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="StorageException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    /// <param name="inner">Underlying cause of the failure.</param>
    public StorageException(string message, Exception inner) : base(message, inner)
    {
    }
}
