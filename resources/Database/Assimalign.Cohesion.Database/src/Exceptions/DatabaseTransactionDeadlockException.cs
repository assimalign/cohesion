using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when a transaction is chosen as the victim of deadlock resolution:
/// its lock request would have closed a wait cycle, so the engine aborted the
/// request instead of letting the cycle stand. Retryable by construction — the
/// surviving transactions make progress, so a fresh attempt can succeed.
/// </summary>
/// <remarks>
/// The area-root surface of the transaction kernel's
/// <c>TransactionDeadlockException</c>, wrapped at the model boundary per the
/// area error policy. On the wire this is an <c>ExecutionFailure</c> whose
/// message names the deadlock; the session stays usable — roll the transaction
/// back and retry.
/// </remarks>
public class DatabaseTransactionDeadlockException : DatabaseTransactionAbortedException
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
}
