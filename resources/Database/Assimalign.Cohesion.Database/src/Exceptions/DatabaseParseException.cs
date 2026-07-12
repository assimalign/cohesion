using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when statement text cannot be parsed in a session's language.
/// </summary>
/// <remarks>
/// Parse failures are a distinct error category from execution failures: the wire
/// protocol maps them to a dedicated error code (<c>ParseFailure</c>), and callers
/// can retry with corrected text without treating the session as faulted. Every
/// model's session throws this from the text-execute seam
/// (<see cref="IDatabaseSession.ExecuteAsync(string, System.Collections.Generic.IReadOnlyDictionary{string, object?}?, System.Threading.CancellationToken)"/>)
/// when its parser rejects the input.
/// </remarks>
public class DatabaseParseException : DatabaseException
{
    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseParseException"/> with a message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DatabaseParseException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseParseException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseParseException(string message, Exception? innerException)
        : base(message, innerException) { }
}
