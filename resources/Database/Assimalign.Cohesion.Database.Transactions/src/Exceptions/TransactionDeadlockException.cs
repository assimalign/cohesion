namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Thrown when a transaction is chosen as the victim of deadlock resolution.
/// The transaction has been rolled back and may be retried by the caller.
/// </summary>
public class TransactionDeadlockException : TransactionAbortedException
{
    /// <summary>
    /// Initializes a new <see cref="TransactionDeadlockException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TransactionDeadlockException(string message)
        : base(message) { }
}
