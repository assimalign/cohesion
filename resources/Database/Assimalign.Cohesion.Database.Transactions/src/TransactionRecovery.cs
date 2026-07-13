using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Recovery-time analysis of the transaction lifecycle records in a storage journal.
/// </summary>
/// <remarks>
/// The rule recovery lives by: <b>a transaction committed if and only if its commit
/// record is durable in the journal.</b> Everything else — a begin without a commit,
/// an explicit rollback, a torn tail — is aborted, and its versions must be purged
/// (<see cref="IVersionStore.PurgeWriterAsync"/>) before the store serves snapshots.
/// </remarks>
public static class TransactionRecovery
{
    /// <summary>
    /// Reads the journal and classifies every transaction sequence it mentions.
    /// </summary>
    /// <param name="journal">The storage journal to analyze.</param>
    /// <returns>The committed and aborted sequences, and the highest sequence observed.</returns>
    public static TransactionRecoveryPlan Analyze(IStorageJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var committed = new HashSet<TransactionSequence>();
        var seen = new HashSet<TransactionSequence>();
        ulong maxSequence = 0;

        foreach (var record in journal.ReadAll())
        {
            if (record.TransactionSequence <= 0)
            {
                continue;
            }

            var sequence = new TransactionSequence((ulong)record.TransactionSequence);
            seen.Add(sequence);

            if (sequence.Value > maxSequence)
            {
                maxSequence = sequence.Value;
            }

            if (record.Type == JournalRecordType.CommitTransaction)
            {
                committed.Add(sequence);
            }
        }

        var aborted = new HashSet<TransactionSequence>(seen);
        aborted.ExceptWith(committed);

        return new TransactionRecoveryPlan(committed, aborted, new TransactionSequence(maxSequence));
    }
}

/// <summary>
/// The result of journal analysis: which transactions committed, which aborted, and
/// the highest sequence observed (the floor for new sequence assignment).
/// </summary>
/// <param name="Committed">Sequences with a durable commit record.</param>
/// <param name="Aborted">Sequences seen in the journal without a durable commit record.</param>
/// <param name="MaxSequence">The highest sequence observed, or zero when the journal is empty.</param>
public sealed record TransactionRecoveryPlan(
    IReadOnlySet<TransactionSequence> Committed,
    IReadOnlySet<TransactionSequence> Aborted,
    TransactionSequence MaxSequence);
