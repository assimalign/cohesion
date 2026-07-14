using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Encodes table rows as MVCC-stamped typed records: a fixed 16-byte version
/// header — the writer and deleter <see cref="TransactionSequence"/> stamps, the
/// B+Tree leaf-entry design adopted for the record space — followed by the
/// owning table's object id and one self-describing component per column, in
/// catalog column order. The object-id prefix is what lets multiple tables share
/// one record space (scans filter by it); the fixed-width stamp header is what
/// makes tombstoning an in-place, same-length update (a deleter stamp never
/// relocates a record) and keeps ADD COLUMN's O(1) null-tail decode intact
/// (stamps sit in front of the tuple, never after the columns).
/// </summary>
internal static class SqlRowCodec
{
    /// <summary>
    /// The current record-space format version, persisted in the catalog: 3 =
    /// stamped records in per-object page chains (rows live on pages tagged with
    /// their table's object id); 2 = stamped records in the shared page stream;
    /// 1 = the pre-MVCC unstamped layout. Older versions upgrade in place when the
    /// database is opened — stamps first (1 → 2), then chain relocation (2 → 3).
    /// The record byte layout itself is unchanged since version 2.
    /// </summary>
    internal const int RecordSpaceFormatVersion = 3;

    /// <summary>
    /// The size of the fixed version-stamp header preceding the tuple payload.
    /// </summary>
    internal const int StampHeaderSize = 16;

    internal static byte[] Encode(ulong objectId, IReadOnlyList<SqlCatalogColumn> columns, object?[] values, TransactionSequence writer)
    {
        var writerCodec = new DatabaseKeyWriter();
        writerCodec.AppendInt64((long)objectId);

        for (int i = 0; i < columns.Count; i++)
        {
            AppendValue(writerCodec, columns[i].Type.Type, values[i]);
        }

        byte[] payload = writerCodec.ToArray();
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
    /// Prepends a zeroed stamp header to a pre-MVCC (format-version-1) record —
    /// the in-place migration write. Writer zero reads as visible to every
    /// snapshot (it precedes every assigned sequence) and deleter zero is "not
    /// deleted", so migrated rows behave exactly as committed bootstrap data.
    /// </summary>
    internal static byte[] UpgradeUnstamped(ReadOnlySpan<byte> record)
    {
        var upgraded = new byte[StampHeaderSize + record.Length];
        record.CopyTo(upgraded.AsSpan(StampHeaderSize));
        return upgraded;
    }

    /// <summary>
    /// Decodes a stamped record when it belongs to the expected table; returns
    /// null when the record belongs to a different object or is too short to
    /// carry a stamp header. Missing trailing columns read as nulls, which is how
    /// ADD COLUMN stays O(1). The version stamps are returned alongside the
    /// values — visibility is the caller's decision, made against its snapshot.
    /// </summary>
    internal static object?[]? TryDecode(
        ReadOnlySpan<byte> record,
        ulong objectId,
        int columnCount,
        out TransactionSequence writer,
        out TransactionSequence deleter)
    {
        writer = default;
        deleter = default;

        if (record.Length < StampHeaderSize)
        {
            return null;
        }

        (writer, deleter) = ReadStamps(record);

        var reader = new DatabaseKeyReader(record.Slice(StampHeaderSize));

        if ((ulong)reader.ReadInt64() != objectId)
        {
            return null;
        }

        var values = new object?[columnCount];

        for (int i = 0; i < columnCount; i++)
        {
            if (reader.IsAtEnd)
            {
                values[i] = null; // column added after this row was written
                continue;
            }

            values[i] = ReadValue(ref reader);
        }

        return values;
    }

    private static void AppendValue(DatabaseKeyWriter writer, DatabaseType type, object? value)
    {
        if (value is null)
        {
            writer.AppendNull();
            return;
        }

        switch (type)
        {
            case DatabaseType.Boolean: writer.AppendBoolean((bool)value); break;
            case DatabaseType.Int8: writer.AppendInt8((sbyte)value); break;
            case DatabaseType.Int16: writer.AppendInt16((short)value); break;
            case DatabaseType.Int32: writer.AppendInt32((int)value); break;
            case DatabaseType.Int64: writer.AppendInt64((long)value); break;
            case DatabaseType.Float32: writer.AppendFloat32((float)value); break;
            case DatabaseType.Float64: writer.AppendFloat64((double)value); break;
            case DatabaseType.Decimal: writer.AppendDecimal((decimal)value); break;
            case DatabaseType.String or DatabaseType.Json: writer.AppendString((string)value, Collation.Binary); break;
            case DatabaseType.Binary or DatabaseType.JsonBinary: writer.AppendBinary((byte[])value); break;
            case DatabaseType.Date: writer.AppendDate((DateOnly)value); break;
            case DatabaseType.Time: writer.AppendTime((TimeOnly)value); break;
            case DatabaseType.DateTime: writer.AppendDateTime((DateTime)value); break;
            case DatabaseType.DateTimeOffset: writer.AppendDateTimeOffset((DateTimeOffset)value); break;
            case DatabaseType.TimeSpan: writer.AppendTimeSpan((TimeSpan)value); break;
            case DatabaseType.Guid: writer.AppendGuid((Guid)value); break;
            default:
                throw new DatabaseException($"Column type {type} cannot be stored yet.");
        }
    }

    private static object? ReadValue(ref DatabaseKeyReader reader)
    {
        return reader.PeekType() switch
        {
            DatabaseType.Null => reader.ReadNull(),
            DatabaseType.Boolean => reader.ReadBoolean(),
            DatabaseType.Int8 => reader.ReadInt8(),
            DatabaseType.Int16 => reader.ReadInt16(),
            DatabaseType.Int32 => reader.ReadInt32(),
            DatabaseType.Int64 => reader.ReadInt64(),
            DatabaseType.Float32 => reader.ReadFloat32(),
            DatabaseType.Float64 => reader.ReadFloat64(),
            DatabaseType.Decimal => reader.ReadDecimal(),
            DatabaseType.String => reader.ReadString(out _),
            DatabaseType.Binary => reader.ReadBinary(),
            DatabaseType.Date => reader.ReadDate(),
            DatabaseType.Time => reader.ReadTime(),
            DatabaseType.DateTime => reader.ReadDateTime(),
            DatabaseType.DateTimeOffset => reader.ReadDateTimeOffset(),
            DatabaseType.TimeSpan => reader.ReadTimeSpan(),
            DatabaseType.Guid => reader.ReadGuid(),
            var other => throw new DatabaseException($"Malformed row: unexpected component type {other}."),
        };
    }
}
