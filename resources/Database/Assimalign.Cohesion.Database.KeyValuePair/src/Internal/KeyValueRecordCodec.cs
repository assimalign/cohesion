using System;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// Encodes key-value entries as MVCC-stamped records: a fixed 16-byte version
/// header — the writer and deleter <see cref="TransactionSequence"/> stamps, the
/// same layout the SQL record space and the B+Tree leaf entries carry — followed
/// by the shared tuple codec payload: the key as one binary component, the value
/// as another. The fixed-width stamp header is what makes tombstoning a
/// same-length in-place update (a deleter stamp never relocates a record); the
/// tuple payload keeps records self-describing for recovery scrubs and scans.
/// </summary>
internal static class KeyValueRecordCodec
{
    /// <summary>
    /// The format version of the database's data storage — entry records and the
    /// primary index tree that shares their file set — this engine reads and
    /// writes, persisted in the catalog: 2 = MVCC-stamped key/value records in the
    /// key space's per-object page chain, indexed by a tree of B-tree page format 2,
    /// which orders entries by key, entry location and writer (#1194). Version 1 —
    /// the same records over a tree of B-tree page format 1, ordered by key alone —
    /// is history: a database is created on this version, and an existing database
    /// with a primary index on any other version is refused at open. There is no
    /// upgrade path (owner decision of 2026-10-02; upgrades are #1152).
    /// </summary>
    internal const int EntrySpaceFormatVersion = 2;

    /// <summary>
    /// The size of the fixed version-stamp header preceding the tuple payload.
    /// </summary>
    internal const int StampHeaderSize = RecordVersionStamp.HeaderSize;

    /// <summary>
    /// Encodes an entry record stamped with its writing transaction's sequence.
    /// </summary>
    internal static byte[] Encode(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, TransactionSequence writer)
    {
        var payloadWriter = new DatabaseKeyWriter(StampHeaderSize + key.Length + value.Length + 16);
        payloadWriter.AppendBinary(key.ToArray());
        payloadWriter.AppendBinary(value.ToArray());

        byte[] payload = payloadWriter.ToArray();
        var record = new byte[StampHeaderSize + payload.Length];
        RecordVersionStamp.WriteWriter(record, writer);
        // Deleter starts at zero (no visible delete); bytes are already zeroed.
        payload.CopyTo(record.AsSpan(StampHeaderSize));
        return record;
    }

    /// <summary>
    /// Reads the version stamps from a stamped record.
    /// </summary>
    internal static (TransactionSequence Writer, TransactionSequence Deleter) ReadStamps(ReadOnlySpan<byte> record)
        => RecordVersionStamp.ReadStamps(record);

    /// <summary>
    /// Returns a same-length copy of a stamped record with the deleter stamp set —
    /// the tombstone write. Same length means the tombstone always rewrites in
    /// place: a delete can never relocate a record.
    /// </summary>
    internal static byte[] WithDeleter(ReadOnlySpan<byte> record, TransactionSequence deleter)
        => RecordVersionStamp.WithDeleter(record, deleter);

    /// <summary>
    /// Returns a same-length copy of a stamped record with the deleter stamp
    /// cleared — the logical undo of a tombstone.
    /// </summary>
    internal static byte[] WithoutDeleter(ReadOnlySpan<byte> record)
        => RecordVersionStamp.WithoutDeleter(record);

    /// <summary>
    /// Decodes a stamped record's key and value. The version stamps are returned
    /// alongside — visibility is the caller's decision, made against its snapshot.
    /// </summary>
    /// <remarks>
    /// Every caller reads the record from the key space's own page chain, so a record that does
    /// not decode is damaged, not another owner's or reclaimed, and the decode throws rather than
    /// offering a result a caller could read as an absent key (#1362). The caller knows the
    /// record's location and reports it as corrupt.
    /// </remarks>
    /// <exception cref="DatabaseTypeException">
    /// The record is too short for its stamp header, its key or value component is malformed or
    /// truncated, or bytes follow the value.
    /// </exception>
    internal static void Decode(
        ReadOnlySpan<byte> record,
        out byte[] key,
        out byte[] value,
        out TransactionSequence writer,
        out TransactionSequence deleter)
    {
        if (record.Length < StampHeaderSize)
        {
            throw new DatabaseTypeException(
                $"The record holds {record.Length} bytes, fewer than its {StampHeaderSize}-byte version-stamp header.");
        }

        (writer, deleter) = ReadStamps(record);

        var reader = new DatabaseKeyReader(record.Slice(StampHeaderSize));
        key = reader.ReadBinary();
        value = reader.ReadBinary();

        if (!reader.IsAtEnd)
        {
            throw new DatabaseTypeException("The record continues past its value component.");
        }
    }
}
