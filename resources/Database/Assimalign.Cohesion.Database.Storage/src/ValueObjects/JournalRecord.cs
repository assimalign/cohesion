using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a single decoded journal (write-ahead log) record.
/// </summary>
/// <param name="Lsn">Monotonically increasing log sequence number.</param>
/// <param name="TransactionSequence">
/// The storage-level transaction sequence that produced the record, or zero for
/// transaction-less records such as checkpoints.
/// </param>
/// <param name="Type">Record type.</param>
/// <param name="PageId">
/// The page a <see cref="JournalRecordType.BeforePageImage"/> or
/// <see cref="JournalRecordType.AfterPageImage"/> record describes; zero otherwise.
/// </param>
/// <param name="Payload">
/// The record payload: the full page image for page-image records, the opaque
/// logical payload for <see cref="JournalRecordType.Operation"/> records, or the
/// packed active-transaction sequences for <see cref="JournalRecordType.Checkpoint"/>.
/// </param>
public readonly record struct JournalRecord(
    long Lsn,
    long TransactionSequence,
    JournalRecordType Type,
    PageId PageId,
    ReadOnlyMemory<byte> Payload);
