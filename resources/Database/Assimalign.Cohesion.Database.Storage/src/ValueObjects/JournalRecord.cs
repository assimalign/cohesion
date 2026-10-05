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
/// The page a <see cref="JournalRecordType.FullPageImage"/>, <see cref="JournalRecordType.PageDelta"/>
/// or <see cref="JournalRecordType.CommittedPageImage"/> record describes; zero otherwise.
/// </param>
/// <param name="Payload">
/// The record payload: for a page record its encoded form (the page's non-zero byte runs for a
/// full page image; the base LSN followed by the changed byte runs for a delta; the base LSN
/// followed by the non-zero byte runs for a committed page image; Storage DESIGN.md, "Page
/// records"), the opaque logical payload for <see cref="JournalRecordType.Operation"/> records,
/// or the packed active-transaction sequences for <see cref="JournalRecordType.Checkpoint"/>.
/// </param>
public readonly record struct JournalRecord(
    long Lsn,
    long TransactionSequence,
    JournalRecordType Type,
    PageId PageId,
    ReadOnlyMemory<byte> Payload);
