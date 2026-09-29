using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Encodes the fixed record-version prefix: an unsigned little-endian writer
/// sequence at bytes 0–7 followed by an unsigned little-endian deleter at bytes 8–15.
/// </summary>
/// <remarks>
/// A zero deleter means no tombstone. The remaining bytes belong to the engine's
/// payload. Changing only the deleter preserves record length and location.
/// </remarks>
public static class RecordVersionStamp
{
    /// <summary>
    /// The size in bytes of the writer/deleter prefix.
    /// </summary>
    public const int HeaderSize = 16;

    /// <summary>
    /// Reads the writer and deleter from a stamped record.
    /// </summary>
    /// <param name="record">A complete record containing at least the stamp prefix.</param>
    /// <returns>The writer and deleter sequences.</returns>
    public static (TransactionSequence Writer, TransactionSequence Deleter) ReadStamps(ReadOnlySpan<byte> record)
    {
        return (
            new TransactionSequence(BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(0, 8))),
            new TransactionSequence(BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(8, 8))));
    }

    /// <summary>
    /// Writes the writer sequence without changing the deleter or payload.
    /// </summary>
    /// <param name="record">The destination record.</param>
    /// <param name="writer">The transaction creating this version.</param>
    public static void WriteWriter(Span<byte> record, TransactionSequence writer)
        => BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(0, 8), writer.Value);

    /// <summary>
    /// Returns a same-length copy with the deleter set, preserving the writer and payload.
    /// </summary>
    /// <param name="record">The record to tombstone.</param>
    /// <param name="deleter">The transaction deleting this version.</param>
    /// <returns>A tombstoned copy of the record.</returns>
    public static byte[] WithDeleter(ReadOnlySpan<byte> record, TransactionSequence deleter)
    {
        var tombstoned = record.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(tombstoned.AsSpan(8, 8), deleter.Value);
        return tombstoned;
    }

    /// <summary>
    /// Returns a same-length copy with the deleter cleared, preserving the writer and payload.
    /// </summary>
    /// <param name="record">The record whose tombstone is being undone.</param>
    /// <returns>A copy with a zero deleter.</returns>
    public static byte[] WithoutDeleter(ReadOnlySpan<byte> record)
    {
        var restored = record.ToArray();
        restored.AsSpan(8, 8).Clear();
        return restored;
    }
}
