using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// An opaque mark of an owner's existing lock grants, created by
/// <see cref="LockManager.MarkStatement"/> before a statement acquires locks or restarts arbitration.
/// </summary>
/// <remarks>
/// Pass the mark to <see cref="LockManager.ReleaseSince"/> only while the statement has
/// applied no writes. Existing resources, including upgrades acquired after the mark,
/// remain held. A mark belongs to one lock manager and one transaction lifetime.
/// </remarks>
public readonly struct LockStatementMark : IEquatable<LockStatementMark>
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

    /// <summary>Determines whether two marks identify the same manager, owner and grant boundary.</summary>
    /// <param name="other">The mark to compare.</param>
    /// <returns>True when both marks identify the same boundary.</returns>
    public bool Equals(LockStatementMark other)
        => ReferenceEquals(Manager, other.Manager) && Owner == other.Owner && GrantSequence == other.GrantSequence;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LockStatementMark other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Manager, Owner, GrantSequence);

    /// <summary>Determines whether two marks are equal.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns>True when the marks identify the same boundary.</returns>
    public static bool operator ==(LockStatementMark left, LockStatementMark right) => left.Equals(right);

    /// <summary>Determines whether two marks differ.</summary>
    /// <param name="left">The first mark.</param>
    /// <param name="right">The second mark.</param>
    /// <returns>True when the marks identify different boundaries.</returns>
    public static bool operator !=(LockStatementMark left, LockStatementMark right) => !left.Equals(right);
}
