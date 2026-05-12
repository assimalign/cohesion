using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents errors that occur during database operations.
/// </summary>
public class DatabaseException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseException"/>.
    /// </summary>
    public DatabaseException() { }

    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseException"/> with a message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DatabaseException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseException(string message, Exception? innerException)
        : base(message, innerException) { }
}
