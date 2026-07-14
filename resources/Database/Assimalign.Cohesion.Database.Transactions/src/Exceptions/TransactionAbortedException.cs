using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Thrown when a transaction is aborted by the engine — by write-write conflict,
/// serialization failure, or deadlock resolution — rather than by the caller.
/// </summary>
/// <remarks>
/// The transactions package is a child root of the Database area and owns an
/// independent exception root (area exception-scoping rule): it does not derive
/// from <c>DatabaseException</c>. A model engine that surfaces an abort through
/// the area's session contract wraps it in a <c>DatabaseException</c> at the
/// model boundary — the same rule the engines already apply to
/// <c>StorageException</c>.
/// </remarks>
public class TransactionAbortedException : Exception
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
