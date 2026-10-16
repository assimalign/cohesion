using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when statement or command text cannot be parsed in a session's language or grammar.
/// </summary>
/// <remarks>
/// Parse failures are a distinct error category from execution failures: the wire
/// protocol maps them to a dedicated error code (<c>ParseFailure</c>), and callers
/// can retry with corrected text without treating the session as faulted. Every
/// language session throws this from its text seam
/// (<see cref="DatabaseLanguageSession{TRequest}.ExecuteAsync(string, System.Collections.Generic.IReadOnlyDictionary{string, object?}?, System.Threading.CancellationToken)"/>)
/// when its parser rejects the input, and so do the models' text-to-request factories
/// (<c>FromSql</c>, <c>FromOql</c>, <c>FromGql</c>). The key-value model has no statement
/// language: its wire server parses each command of its grammar into a typed request and
/// reports a command that fails to parse with this exception.
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
