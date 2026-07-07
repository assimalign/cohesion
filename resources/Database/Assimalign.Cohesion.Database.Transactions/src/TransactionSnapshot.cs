using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// An immutable MVCC visibility snapshot: the view of committed transaction state
/// a transaction (or statement) reads through.
/// </summary>
/// <remarks>
/// A snapshot captures the oldest sequence still in-flight when it was taken
/// (<see cref="Minimum"/>), the next sequence to be assigned (<see cref="Maximum"/>),
/// and the set of sequences that were active (begun but not committed) at capture time.
/// A row version written by sequence <c>x</c> is visible when <c>x</c> committed before
/// the snapshot was taken — that is, <c>x &lt; Maximum</c> and <c>x</c> is not in the
/// active set. The snapshot owner always sees its own writes.
/// </remarks>
public sealed class TransactionSnapshot
{
    private readonly FrozenSet<ulong> _active;

    /// <summary>
    /// Initializes a new <see cref="TransactionSnapshot"/>.
    /// </summary>
    /// <param name="owner">The sequence of the transaction that owns this snapshot.</param>
    /// <param name="minimum">The oldest sequence that was still active when the snapshot was taken.</param>
    /// <param name="maximum">The next sequence that had not yet been assigned when the snapshot was taken.</param>
    /// <param name="active">The sequences active (begun, not committed) when the snapshot was taken.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="minimum"/> is greater than <paramref name="maximum"/>.</exception>
    public TransactionSnapshot(
        TransactionSequence owner,
        TransactionSequence minimum,
        TransactionSequence maximum,
        IEnumerable<TransactionSequence> active)
    {
        if (minimum > maximum)
        {
            throw new ArgumentException("Snapshot minimum cannot exceed its maximum.", nameof(minimum));
        }
        ArgumentNullException.ThrowIfNull(active);

        Owner = owner;
        Minimum = minimum;
        Maximum = maximum;

        var set = new HashSet<ulong>();
        foreach (var sequence in active)
        {
            set.Add(sequence.Value);
        }
        _active = set.ToFrozenSet();
    }

    /// <summary>
    /// Gets the sequence of the transaction that owns this snapshot.
    /// </summary>
    public TransactionSequence Owner { get; }

    /// <summary>
    /// Gets the oldest sequence that was still active when the snapshot was taken.
    /// Every version written by a sequence below this bound is decided (committed or aborted).
    /// </summary>
    public TransactionSequence Minimum { get; }

    /// <summary>
    /// Gets the next sequence that had not yet been assigned when the snapshot was taken.
    /// Versions written at or above this bound are never visible.
    /// </summary>
    public TransactionSequence Maximum { get; }

    /// <summary>
    /// Determines whether a row version written by the specified transaction sequence
    /// is visible through this snapshot.
    /// </summary>
    /// <param name="writer">The sequence of the transaction that wrote the version.</param>
    /// <returns>True when the version is visible to the snapshot owner; otherwise false.</returns>
    public bool IsVisible(TransactionSequence writer)
    {
        // A transaction always sees its own writes.
        if (writer == Owner)
        {
            return true;
        }
        // Written after the snapshot was taken.
        if (writer >= Maximum)
        {
            return false;
        }
        // Below the minimum every transaction is decided; between minimum and
        // maximum the writer is visible only if it was not still in flight.
        if (writer < Minimum)
        {
            return true;
        }
        return !_active.Contains(writer.Value);
    }
}
