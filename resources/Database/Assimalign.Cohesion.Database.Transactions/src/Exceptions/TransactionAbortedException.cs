using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Thrown when a transaction is aborted by the engine — by write-write conflict,
/// serialization failure, or deadlock resolution — rather than by the caller.
/// </summary>
public class TransactionAbortedException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="TransactionAbortedException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TransactionAbortedException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new <see cref="TransactionAbortedException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public TransactionAbortedException(string message, Exception? innerException)
        : base(message, innerException) { }
}
