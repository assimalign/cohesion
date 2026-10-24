using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents errors that occur during database operations.
/// </summary>
public class DatabaseException : Exception
{
    /// <summary>Gets the complete diagnostics, ordered by source offset; empty when none were supplied.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Array.Empty<Diagnostic>();

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

    /// <summary>Initializes a database failure with its complete structured diagnostics.</summary>
    /// <param name="message">The error message, without a diagnostic code prefix.</param>
    /// <param name="diagnostics">The diagnostics, copied and ordered by start offset; unlocated diagnostics follow located ones.</param>
    /// <param name="innerException">The underlying cause, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
    public DatabaseException(string message, IEnumerable<Diagnostic> diagnostics, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostics = Array.AsReadOnly(diagnostics.OrderBy(diagnostic => diagnostic.Start is null).ThenBy(diagnostic => diagnostic.Start).ToArray());
    }

    internal static DatabaseException CreateRootFailure(string code, string message)
        => new(message, [new Diagnostic { Code = code, Message = message, Severity = DiagnosticSeverity.Error }]);
}
