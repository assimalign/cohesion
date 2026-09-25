using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when an operation requires a logical database that does not exist.
/// </summary>
/// <remarks>
/// This exception is the precise absence signal for
/// <see cref="IDatabaseEngine.OpenDatabaseAsync(DatabaseName, System.Threading.CancellationToken)"/>.
/// Callers may distinguish a missing database from other failures that prevent an
/// existing database from being opened.
/// </remarks>
public class DatabaseNotFoundException : DatabaseException
{
    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseNotFoundException"/> with a message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DatabaseNotFoundException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseNotFoundException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseNotFoundException(string message, Exception? innerException)
        : base(message, innerException) { }
}
