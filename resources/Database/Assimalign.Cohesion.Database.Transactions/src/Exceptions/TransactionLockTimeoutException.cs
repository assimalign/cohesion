using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// An individual lock wait exceeded its configured timeout (SQLSTATE 55P03, lock_not_available).
/// The queued request has been removed; the owner's existing grants and transaction remain active.
/// </summary>
public sealed class TransactionLockTimeoutException : TimeoutException
{
    private static readonly string _message = ErrorMessages.LockTimeout;

    internal TransactionLockTimeoutException(Exception cause) : base(_message, cause)
    {
    }
}
