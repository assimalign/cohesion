using System;
using System.Buffers.Binary;
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
    public static TransactionRecoveryPlan Analyze(IStorageJournal journal) => Analyze(journal, []);

    /// <summary>
    /// Reads the journal and classifies every transaction sequence it mentions, and every
    /// writer the storage's checkpoint anchor recorded as in flight.
    /// </summary>
    /// <param name="journal">The storage journal to analyze.</param>
    /// <param name="checkpointActiveTransactions">
    /// The sequences the last checkpoint recorded in the storage's file header
    /// (<see cref="Storage.CheckpointActiveTransactions"/>): the coordinator's in-flight
    /// writers, the only transactions whose row versions can be in the data pages, in any
    /// number (a header slot chains anchor pages for those it cannot hold). Each one is
    /// classified exactly like a sequence a checkpoint record lists: aborted unless the
    /// journal holds its commit record. The anchor is what still names them when the
    /// checkpoint's own record was lost after the journal truncation. A reader is not
    /// listed and needs no classification; one that became a writer after the checkpoint
    /// was announced again with a begin record, which the journal scan classifies.
    /// </param>
    /// <returns>The committed and aborted sequences, and the highest sequence observed.</returns>
    /// <remarks>
    /// <see cref="TransactionRecoveryPlan.MaxSequence"/> covers what the journal and the
    /// anchor name. A reader whose begin record a checkpoint truncated is in neither, so it
    /// is no floor for new sequences; the storage's own sequence floor
    /// (<see cref="IStorage.ReserveTransactionSequence"/> resumes above it) is.
    /// </remarks>
    public static TransactionRecoveryPlan Analyze(IStorageJournal journal, IEnumerable<long> checkpointActiveTransactions)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(checkpointActiveTransactions);

        var committed = new HashSet<TransactionSequence>();
        var seen = new HashSet<TransactionSequence>();
        ulong maxSequence = 0;

        foreach (long anchored in checkpointActiveTransactions)
        {
            if (anchored <= 0)
            {
                continue;
            }

            var anchoredSequence = new TransactionSequence((ulong)anchored);
            seen.Add(anchoredSequence);

            if (anchoredSequence.Value > maxSequence)
            {
                maxSequence = anchoredSequence.Value;
            }
        }

        IEnumerable<JournalRecord> records = journal is StorageJournal streaming
            ? streaming.ReadSequential()
            : journal.ReadAll();
        foreach (var record in records)
        {
            // A checkpoint record's payload lists the writers that were still in
            // flight when the journal was truncated — their begin records were
            // dropped by the truncation, so the checkpoint is their classification
            // anchor: a listed sequence with no later commit record is aborted,
            // exactly as if its begin record survived.
            if (record.Type == JournalRecordType.Checkpoint)
            {
                var payload = record.Payload.Span;

                for (int offset = 0; offset + sizeof(long) <= payload.Length; offset += sizeof(long))
                {
                    long active = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));

                    if (active <= 0)
                    {
                        continue;
                    }

                    var activeSequence = new TransactionSequence((ulong)active);
                    seen.Add(activeSequence);

                    if (activeSequence.Value > maxSequence)
                    {
                        maxSequence = activeSequence.Value;
                    }
                }

                continue;
            }

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
