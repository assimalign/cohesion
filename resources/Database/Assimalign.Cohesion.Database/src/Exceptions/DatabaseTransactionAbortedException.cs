using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when the engine aborts a transaction rather than the caller — a
/// write-write conflict, a deadlock resolution, or a commit whose record could
/// not be made durable. The transaction's effects are undone and the operation
/// is retryable by construction: a fresh transaction re-attempting the same work
/// can succeed.
/// </summary>
/// <remarks>
/// This is the area-root surface of the transaction kernel's independent
/// exception root (<c>TransactionAbortedException</c> in
/// <c>Assimalign.Cohesion.Database.Transactions</c>): model engines translate at
/// their boundary per the area error policy, so sessions and wire clients observe
/// a <see cref="DatabaseException"/>-derived type (an <c>ExecutionFailure</c> on
/// the wire) while in-process consumers can catch the abort kind precisely and
/// retry.
/// </remarks>
public class DatabaseTransactionAbortedException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="DatabaseTransactionAbortedException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DatabaseTransactionAbortedException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new <see cref="DatabaseTransactionAbortedException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseTransactionAbortedException(string message, Exception? innerException)
        : base(message, innerException) { }
}
