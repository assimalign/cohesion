using System;
using System.Buffers.Binary;

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
    /// The current entry-space format version, persisted in the catalog: 1 =
    /// MVCC-stamped key/value records in the key space's per-object page chain.
    /// The model was born on this format — there is no upgrade machinery, only a
    /// too-new rejection at open.
    /// </summary>
    internal const int EntrySpaceFormatVersion = 1;

    /// <summary>
    /// The size of the fixed version-stamp header preceding the tuple payload.
    /// </summary>
    internal const int StampHeaderSize = 16;

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
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), writer.Value);
        // Deleter starts at zero (no visible delete); bytes are already zeroed.
        payload.CopyTo(record.AsSpan(StampHeaderSize));
        return record;
    }

    /// <summary>
    /// Reads the version stamps from a stamped record.
    /// </summary>
    internal static (TransactionSequence Writer, TransactionSequence Deleter) ReadStamps(ReadOnlySpan<byte> record)
    {
        return (
            new TransactionSequence(BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(0, 8))),
            new TransactionSequence(BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(8, 8))));
    }

    /// <summary>
    /// Returns a same-length copy of a stamped record with the deleter stamp set —
    /// the tombstone write. Same length means the tombstone always rewrites in
    /// place: a delete can never relocate a record.
    /// </summary>
    internal static byte[] WithDeleter(ReadOnlySpan<byte> record, TransactionSequence deleter)
    {
        var tombstoned = record.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(tombstoned.AsSpan(8, 8), deleter.Value);
        return tombstoned;
    }

    /// <summary>
    /// Returns a same-length copy of a stamped record with the deleter stamp
    /// cleared — the logical undo of a tombstone.
    /// </summary>
    internal static byte[] WithoutDeleter(ReadOnlySpan<byte> record)
    {
        var restored = record.ToArray();
        restored.AsSpan(8, 8).Clear();
        return restored;
    }

    /// <summary>
    /// Decodes a stamped record's key and value; returns false when the record is
    /// too short to carry a stamp header or the payload is malformed. The version
    /// stamps are returned alongside — visibility is the caller's decision, made
    /// against its snapshot.
    /// </summary>
    internal static bool TryDecode(
        ReadOnlySpan<byte> record,
        out byte[] key,
        out byte[] value,
        out TransactionSequence writer,
        out TransactionSequence deleter)
    {
        key = [];
        value = [];
        writer = default;
        deleter = default;

        if (record.Length < StampHeaderSize)
        {
            return false;
        }

        (writer, deleter) = ReadStamps(record);

        try
        {
            var reader = new DatabaseKeyReader(record.Slice(StampHeaderSize));
            key = reader.ReadBinary();
            value = reader.ReadBinary();
            return reader.IsAtEnd;
        }
        catch (DatabaseTypeException)
        {
            return false;
        }
    }
}
