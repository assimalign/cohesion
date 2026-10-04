using System;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Encodes table rows as MVCC-stamped typed records: a fixed 16-byte version
/// header — the writer and deleter <see cref="TransactionSequence"/> stamps, the
/// B+Tree leaf-entry design adopted for the record space — followed by the
/// owning table's object id and one self-describing component per physical
/// column (<see cref="SqlCatalogTable.PhysicalColumnCount"/>), live or dropped, in
/// physical-ordinal order. The object-id prefix is what lets multiple tables share
/// one record space (scans filter by it); the fixed-width stamp header is what
/// makes tombstoning an in-place, same-length update (a deleter stamp never
/// relocates a record) and keeps ADD COLUMN's missing-tail decode intact
/// (stamps sit in front of the tuple, never after the columns).
/// </summary>
/// <remarks>
/// A dropped column keeps its physical ordinal (#1241): a version written before the
/// drop still stores its value there, and one written after stores NULL, as
/// PostgreSQL's INSERT and UPDATE store a null for a dropped attribute
/// (<c>src/backend/optimizer/prep/preptlist.c:446-455</c>, <c>expand_targetlist</c>).
/// Decoding walks every physical component and skips the dropped ones without
/// materializing them, as <c>heap_deform_tuple</c> walks a dropped attribute by its
/// stored length (<c>src/backend/access/common/heaptuple.c:1254</c>). Because a physical
/// ordinal is never reused, a version decodes onto the right columns under every
/// definition of its table: one written before or after a drop or an addition, read
/// through a definition bound before or after it.
/// </remarks>
internal static class SqlRowCodec
{
    /// <summary>
    /// The format version of the database's data storage (rows and the index
    /// trees that share its file set) this engine reads and writes, persisted in
    /// the catalog: 6 = format 5's stamped records and index trees, with each
    /// table's physical column layout in its catalog record (table-record extension
    /// version 3): a dropped column keeps its physical ordinal, rows are decoded
    /// through that layout, and DROP COLUMN rewrites no row (#1241). Earlier
    /// versions — 5 (DROP COLUMN spliced the column out of every stored version, so
    /// the catalog recorded no dropped columns; index keys encode the temporal
    /// identity, <see cref="ToKeyIdentity"/>, in index trees of B-tree page format 2,
    /// which order entries by key, entry reference and writer, #1194), 4 (the same
    /// rows, with index trees of B-tree page format 1, ordered by key alone), 3 (the
    /// kind and offset inside temporal keys), 2 (records in the shared page stream)
    /// and 1 (pre-MVCC unstamped records) — are history: a database is created on
    /// this version, and an existing one on any other version is refused at open.
    /// There is no upgrade path (owner decisions of 2026-10-01 and 2026-10-02;
    /// upgrades are #1152).
    /// </summary>
    internal const int RecordSpaceFormatVersion = 6;

    /// <summary>
    /// The size of the fixed version-stamp header preceding the tuple payload.
    /// </summary>
    internal const int StampHeaderSize = RecordVersionStamp.HeaderSize;

    /// <summary>
    /// Encodes a row version under a table definition: one component per physical column,
    /// the value of each live column and NULL at each dropped ordinal.
    /// </summary>
    /// <param name="table">The definition the version is written under.</param>
    /// <param name="values">The live columns' values, by position in <see cref="SqlCatalogTable.Columns"/>.</param>
    /// <param name="writer">The writing transaction's stamp.</param>
    /// <returns>The stamped record.</returns>
    internal static byte[] Encode(SqlCatalogTable table, object?[] values, TransactionSequence writer)
    {
        var writerCodec = new DatabaseKeyWriter();
        writerCodec.AppendInt64((long)table.ObjectId);

        var dropped = table.DroppedColumnOrdinals;
        for (int physical = 0, column = 0, next = 0; physical < table.PhysicalColumnCount; physical++)
        {
            if (next < dropped.Count && dropped[next] == physical)
            {
                next++;
                writerCodec.AppendNull();
                continue;
            }

            AppendValue(writerCodec, table.Columns[column].Type.Type, values[column]);
            column++;
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
    /// Decodes a stamped record through a table definition when it belongs to that
    /// table; returns null when the record belongs to a different object or is too short
    /// to carry a stamp header. Every physical component up to the definition's
    /// <see cref="SqlCatalogTable.PhysicalColumnCount"/> is walked; a component at a
    /// dropped ordinal is skipped without being materialized, and components past the
    /// definition's physical columns (written under a later definition) are ignored.
    /// Returns how many live columns the record stores, so the caller can resolve absent
    /// trailing fields from its bound catalog definition without confusing them with
    /// explicitly stored NULLs: physical ordinals ascend with the live columns, so the
    /// stored columns are always a prefix. Visibility is the caller's decision, made
    /// against its snapshot and the returned version stamps.
    /// </summary>
    /// <param name="record">The stored record.</param>
    /// <param name="table">The definition to decode through.</param>
    /// <param name="writer">The record's writer stamp.</param>
    /// <param name="deleter">The record's deleter stamp.</param>
    /// <param name="storedColumnCount">How many of the definition's live columns the record stores.</param>
    /// <returns>The live columns' values, by position in <see cref="SqlCatalogTable.Columns"/>, or null.</returns>
    internal static object?[]? TryDecode(
        ReadOnlySpan<byte> record,
        SqlCatalogTable table,
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

        if ((ulong)reader.ReadInt64() != table.ObjectId)
        {
            return null;
        }

        var values = new object?[table.Columns.Count];
        var dropped = table.DroppedColumnOrdinals;

        for (int physical = 0, next = 0; physical < table.PhysicalColumnCount && !reader.IsAtEnd; physical++)
        {
            // A record that ends early was written before the columns it lacks were added.
            if (next < dropped.Count && dropped[next] == physical)
            {
                next++;
                reader.Skip();
                continue;
            }

            values[storedColumnCount++] = ReadValue(ref reader);
        }

        return values;
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
