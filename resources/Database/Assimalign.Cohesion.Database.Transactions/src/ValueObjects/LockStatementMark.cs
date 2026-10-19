namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// An opaque mark of an owner's existing lock grants, created by
/// <see cref="LockManager.MarkStatement"/> before a statement acquires locks or writes.
/// </summary>
/// <remarks>
/// Pass the mark to <see cref="LockManager.ReleaseSince"/> only while the statement has
/// applied no writes. Existing resources, including upgrades acquired after the mark,
/// remain held. A mark belongs to one lock manager and one transaction lifetime.
/// </remarks>
public readonly struct LockStatementMark
{
    internal LockStatementMark(LockManager manager, TransactionSequence owner, ulong grantSequence)
    {
        Manager = manager;
        Owner = owner;
        GrantSequence = grantSequence;
    }

    internal LockManager? Manager { get; }
    internal TransactionSequence Owner { get; }
    internal ulong GrantSequence { get; }
}
