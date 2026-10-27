using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when a transaction is chosen as the victim of deadlock resolution:
/// its lock request would have closed a wait cycle, so the engine aborted the
/// request instead of letting the cycle stand. After the caller rolls the victim
/// transaction back, surviving transactions can progress and a fresh attempt can succeed.
/// </summary>
/// <remarks>
/// The area-root surface of the transaction kernel's
/// <c>TransactionDeadlockException</c>, wrapped at the model boundary per the
/// area error policy. On the wire this is an <c>ExecutionFailure</c> whose
/// diagnostics identify the deadlock when supplied by the model. Under statement atomicity,
/// only the failed statement is undone; earlier transaction work and locks remain until
/// the caller rolls the victim transaction back and retries. The session stays usable.
/// </remarks>
public sealed class DatabaseTransactionDeadlockException : DatabaseTransactionAbortedException
{
    /// <summary>
    /// Initializes a new <see cref="DatabaseTransactionDeadlockException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DatabaseTransactionDeadlockException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new <see cref="DatabaseTransactionDeadlockException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseTransactionDeadlockException(string message, Exception? innerException)
        : base(message, innerException) { }

    /// <summary>Initializes a deadlock refusal with its complete structured diagnostics.</summary>
    /// <param name="message">The error message, without a diagnostic code prefix.</param>
    /// <param name="diagnostics">The diagnostics, copied and ordered by start offset; unlocated diagnostics follow located ones.</param>
    /// <param name="innerException">The underlying cause, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
    public DatabaseTransactionDeadlockException(string message, IEnumerable<Diagnostic> diagnostics, Exception? innerException = null)
        : base(message, diagnostics, innerException) { }
}
