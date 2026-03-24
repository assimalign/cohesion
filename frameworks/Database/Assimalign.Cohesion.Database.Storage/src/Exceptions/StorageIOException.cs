using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents an error that occurs during storage I/O operations such as
/// reading or writing pages to the underlying stream.
/// </summary>
public sealed class StorageIOException : StorageException
{
    /// <summary>
    /// Initializes a new instance of <see cref="StorageIOException"/> with the specified message.
    /// </summary>
    /// <param name="message">A message describing the error.</param>
    public StorageIOException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="StorageIOException"/> with the specified message and inner exception.
    /// </summary>
    /// <param name="message">A message describing the error.</param>
    /// <param name="inner">The inner exception that caused this error.</param>
    public StorageIOException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
