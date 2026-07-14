namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Represents the lifecycle state of a database transaction.
/// </summary>
public enum TransactionState : byte
{
    /// <summary>The transaction is active and accepting operations.</summary>
    Active = 0,
    /// <summary>The transaction has been committed.</summary>
    Committed,
    /// <summary>The transaction has been rolled back.</summary>
    RolledBack,
    /// <summary>The transaction encountered an error.</summary>
    Faulted
}
