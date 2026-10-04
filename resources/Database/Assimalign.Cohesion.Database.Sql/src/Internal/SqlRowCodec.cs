using System;
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
/// relocates a record) and keeps ADD COLUMN's missing-tail decode intact
/// (stamps sit in front of the tuple, never after the columns).
/// </summary>
internal static class SqlRowCodec
{
    /// <summary>
    /// The format version of the database's data storage (rows and the index
    /// trees that share its file set) this engine reads and writes, persisted in
    /// the catalog: 5 = stamped records in per-object page chains whose index
    /// keys encode the temporal identity (<see cref="ToKeyIdentity"/>), in index
    /// trees of B-tree page format 2, which order entries by key, entry reference
    /// and writer (#1194). Earlier versions — 4 (the same rows, with index trees
    /// of B-tree page format 1, ordered by key alone), 3 (the kind and offset
    /// inside temporal keys), 2 (records in the shared page stream) and 1
    /// (pre-MVCC unstamped records) — are history: a database is created on this
    /// version, and an existing one on any other version is refused at open.
    /// There is no upgrade path (owner decisions of 2026-10-01 and 2026-10-02;
    /// upgrades are #1152).
    /// </summary>
    internal const int RecordSpaceFormatVersion = 5;

    /// <summary>
    /// The size of the fixed version-stamp header preceding the tuple payload.
    /// </summary>
    internal const int StampHeaderSize = RecordVersionStamp.HeaderSize;

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
    /// Decodes a stamped record when it belongs to the expected table; returns
    /// null when the record belongs to a different object or is too short to
    /// carry a stamp header. Returns the stored column count so the caller can
    /// resolve absent trailing fields from its bound catalog definition without
    /// confusing them with explicitly stored NULLs. Visibility is the caller's
    /// decision, made against its snapshot and the returned version stamps.
    /// </summary>
    internal static object?[]? TryDecode(
        ReadOnlySpan<byte> record,
        ulong objectId,
        int columnCount,
        out TransactionSequence writer,
        out TransactionSequence deleter,
        out int storedColumnCount)
    {
        writer = default;
        deleter = default;
        storedColumnCount = 0;

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
                break; // column added after this row version was written
            }

            values[i] = ReadValue(ref reader);
            storedColumnCount++;
        }

        return values;
    }

    /// <summary>
    /// Returns a copy of a stamped record with the component of one stored column
    /// removed: DROP COLUMN's row rewrite (#1237). Every other byte is copied
    /// verbatim (the version stamps, the object id and each surviving component keep
    /// their exact encoding), so the copy is shorter than <paramref name="record"/> by
    /// exactly the removed component, at least one byte. A slotted page writes a
    /// record no longer than its slot in place, at the same page and slot, so the
    /// rewrite never moves a version and every reference to it stays valid: index
    /// entries (key, entry reference, writer) and the version store's locations.
    /// </summary>
    /// <param name="record">The stored record.</param>
    /// <param name="objectId">The table the record must belong to.</param>
    /// <param name="ordinal">The ordinal, in the layout the record was written under, of the column to remove.</param>
    /// <returns>
    /// The record without the column's component, or null when the record needs no
    /// rewrite: it is too short to carry a stamp header, belongs to another object, or
    /// stores no component at <paramref name="ordinal"/>. The last case is a version
    /// written before the column was added: the column is part of its missing tail,
    /// which decodes from the column metadata, and the components it does store keep
    /// their positions.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    internal static byte[]? WithoutColumn(ReadOnlySpan<byte> record, ulong objectId, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        if (record.Length < StampHeaderSize)
        {
            return null;
        }

        var reader = new DatabaseKeyReader(record.Slice(StampHeaderSize));

        if ((ulong)reader.ReadInt64() != objectId)
        {
            return null;
        }

        for (int i = 0; i < ordinal; i++)
        {
            if (reader.IsAtEnd)
            {
                return null;
            }

            ReadValue(ref reader);
        }

        if (reader.IsAtEnd)
        {
            return null;
        }

        int start = StampHeaderSize + reader.BytesConsumed;
        ReadValue(ref reader);
        int end = StampHeaderSize + reader.BytesConsumed;

        var spliced = new byte[record.Length - (end - start)];
        record[..start].CopyTo(spliced);
        record[end..].CopyTo(spliced.AsSpan(start));
        return spliced;
    }

    /// <summary>
    /// Appends one typed value as an index-key component: the identity encoding
    /// every key path shares (maintenance, seek bounds, unique-key locks and build
    /// duplicate detection). Strings encode under the column's effective
    /// collation and temporal values encode their <see cref="ToKeyIdentity"/>
    /// form, so for every key type except floating point two keys are byte-equal
    /// exactly when <see cref="SqlValueComparer"/> calls their values equal.
    /// Floating keys keep the IEEE bytes, so positive and negative zero stay
    /// distinct keys although SQL calls them equal; that is why the planner never
    /// seeks a signed-zero equality or a floating range, and joins never seek
    /// floating keys.
    /// </summary>
    internal static void AppendKeyValue(DatabaseKeyWriter writer, DatabaseType type, object? value, Collation collation)
        => AppendValue(writer, type, ToKeyIdentity(value), collation);

    /// <summary>
    /// Maps a value to the canonical member of its SQL equality class for key
    /// encoding (#1099). A <c>TIMESTAMP</c> keeps its ticks and drops its
    /// <see cref="DateTimeKind"/> (encoded as <see cref="DateTimeKind.Unspecified"/>;
    /// no time-zone conversion happens, matching the comparer); a
    /// <c>TIMESTAMPTZ</c> becomes the same instant at offset zero. Every other
    /// value is returned unchanged. Rows never pass through this mapping — they
    /// keep the written kind and offset.
    /// </summary>
    internal static object? ToKeyIdentity(object? value) => value switch
    {
        DateTime timestamp when timestamp.Kind != DateTimeKind.Unspecified
            => DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
        DateTimeOffset instant when instant.Offset != TimeSpan.Zero
            => instant.ToUniversalTime(),
        _ => value,
    };

    /// <summary>
    /// Appends one typed value as a self-describing, order-preserving component —
    /// the row encoding, which round-trips the written value exactly (original
    /// strings under Binary, DateTime kinds and DateTimeOffset offsets). Index
    /// keys go through <see cref="AppendKeyValue"/> instead.
    /// </summary>
    internal static void AppendValue(DatabaseKeyWriter writer, DatabaseType type, object? value, Collation? collation = null)
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
            case DatabaseType.String or DatabaseType.Json: writer.AppendString((string)value, collation ?? Collation.Binary); break;
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
