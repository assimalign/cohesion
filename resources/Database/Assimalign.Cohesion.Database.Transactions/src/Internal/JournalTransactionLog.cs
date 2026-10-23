using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Transaction log bound to the storage write-ahead log: lifecycle records ride the
/// same journal as page images, and commit acknowledges only after the journal is
/// durable up to the commit record (the write-ahead rule). Group commit falls out of
/// the journal's <see cref="StorageJournal.EnsureDurable"/> — a flush that covers one
/// commit covers every earlier record, so concurrent commits share fsyncs.
/// </summary>
internal sealed class JournalTransactionLog : TransactionLog
{
    private readonly StorageJournal _journal;
    private readonly HashSet<ulong> _writers = new();

    // Enrollment and ends take this gate before the journal's monitor. The manager releases
    // its context/active-table locks first; durable waits run after this gate is released.
    private readonly object _gate = new();

    internal JournalTransactionLog(StorageJournal journal)
    {
        _journal = journal;
    }

    /// <inheritdoc />
    public override ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }

    /// <inheritdoc />
    public override void EnterWriter(TransactionSequence sequence)
    {
        lock (_gate)
        {
            if (_writers.Contains(sequence.Value))
            {
                return;
            }

            _journal.AppendBegin((long)sequence.Value);
            _writers.Add(sequence.Value);
        }
    }

    /// <inheritdoc />
    public override ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long lsn;
        lock (_gate)
        {
            if (!_writers.Contains(sequence.Value))
            {
                return default;
            }

            lsn = _journal.AppendCommit((long)sequence.Value);
            _writers.Remove(sequence.Value);
        }

        try
        {
            _journal.EnsureDurable(lsn);
        }
        catch (Exception exception)
        {
            // The record is in the journal, which recovery reads as committed: the
            // transaction can no longer abort, only its durability is open.
            throw new TransactionCommitUnconfirmedException(UnconfirmedMessage(sequence), exception);
        }

        return default;
    }

    /// <summary>
    /// The message of a commit whose record was appended but whose durable flush failed, which
    /// took the journal's storage offline (#1243).
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    internal static string UnconfirmedMessage(TransactionSequence sequence)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.LogUnconfirmedMessageFailure, sequence);

    /// <inheritdoc />
    public override ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_writers.Remove(sequence.Value))
            {
                // Undo is complete: even a refused advisory record must forget this writer.
                _journal.AppendRollback((long)sequence.Value);
            }
        }

        return default;
    }
}
