using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when an operation requires a logical database that does not exist.
/// </summary>
/// <remarks>
/// This exception is the precise absence signal for
/// <see cref="DatabaseEngine.OpenDatabaseAsync(DatabaseName, System.Threading.CancellationToken)"/>.
/// Callers may distinguish a missing database from other failures that prevent an
/// existing database from being opened.
/// </remarks>
public sealed class DatabaseNotFoundException : DatabaseException
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
    /// <param name="innerException">The underlying cause. Structured diagnostics are preserved when it is a <see cref="DatabaseException"/>.</param>
    public DatabaseNotFoundException(string message, Exception? innerException)
        : base(message, (innerException as DatabaseException)?.Diagnostics ?? [], innerException) { }

    /// <summary>Initializes precise database absence with copied structured diagnostics.</summary>
    /// <param name="message">The error message without a diagnostic code prefix.</param>
    /// <param name="diagnostics">The diagnostics, copied and ordered by source offset.</param>
    /// <param name="innerException">The original cause, or null when absence has no underlying cause.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
    /// <remarks>The explicit third argument preserves the existing two-argument null-inner call.</remarks>
    public DatabaseNotFoundException(string message, IEnumerable<Diagnostic> diagnostics, Exception? innerException)
        : base(message, diagnostics, innerException) { }
}
