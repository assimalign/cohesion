using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

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
/// A parsed typed statement with error diagnostics has the same contract: the exception
/// carries the complete diagnostic list and an error-recovery tree is never executed.
/// </remarks>
public sealed class DatabaseParseException : DatabaseException
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

    /// <summary>Initializes a parse failure carrying every diagnostic from the rejected statement.</summary>
    /// <param name="message">The existing first-error text.</param>
    /// <param name="diagnostics">The statement's complete diagnostics.</param>
    /// <param name="innerException">The underlying cause, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
    public DatabaseParseException(string message, IEnumerable<Diagnostic> diagnostics, Exception? innerException = null)
        : base(message, diagnostics, innerException) { }
}
