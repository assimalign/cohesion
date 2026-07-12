using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Encodes table rows as typed records: the owning table's object id followed by
/// one self-describing component per column, in catalog column order. The object-id
/// prefix is what lets multiple tables share one record space — scans filter by it.
/// </summary>
internal static class SqlRowCodec
{
    internal static byte[] Encode(ulong objectId, IReadOnlyList<SqlCatalogColumn> columns, object?[] values)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt64((long)objectId);

        for (int i = 0; i < columns.Count; i++)
        {
            AppendValue(writer, columns[i].Type.Type, values[i]);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Decodes a record when it belongs to the expected table; returns null when the
    /// record belongs to a different object (or predates the table's newest columns —
    /// missing trailing columns read as nulls, which is how ADD COLUMN stays O(1)).
    /// </summary>
    internal static object?[]? TryDecode(ReadOnlySpan<byte> record, ulong objectId, int columnCount)
    {
        var reader = new DatabaseKeyReader(record);

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
