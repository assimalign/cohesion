using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// The declared mapping between SQL type names and the shared type system
/// (<see cref="DatabaseType"/>). This is the single translation table catalogs,
/// planners, and tooling use — the authoritative prose matrix lives in
/// <c>docs/DIALECT.md</c> and must change in the same commit as this table.
/// </summary>
public static class SqlTypeNames
{
    /// <summary>
    /// Resolves a SQL type name (as written in DDL or CAST) to its shared type
    /// identity, honoring optional length/precision/scale arguments.
    /// </summary>
    /// <param name="name">The SQL type name, case-insensitive (e.g. <c>VARCHAR</c>, <c>BIGINT</c>).</param>
    /// <param name="length">The declared length argument, when one was written.</param>
    /// <param name="precision">The declared precision argument, when one was written.</param>
    /// <param name="scale">The declared scale argument, when one was written.</param>
    /// <param name="typeInfo">The resolved type identity and constraints.</param>
    /// <returns>True when the name is part of the declared dialect; otherwise false.</returns>
    public static bool TryResolve(string name, int? length, int? precision, int? scale, out DatabaseTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(name);

        DatabaseType? type = name.ToUpperInvariant() switch
        {
            "BOOLEAN" or "BOOL" => DatabaseType.Boolean,
            "TINYINT" => DatabaseType.Int8,
            "SMALLINT" or "INT2" => DatabaseType.Int16,
            "INT" or "INTEGER" or "INT4" => DatabaseType.Int32,
            "BIGINT" or "INT8" => DatabaseType.Int64,
            "REAL" or "FLOAT4" => DatabaseType.Float32,
            "FLOAT" or "FLOAT8" or "DOUBLE" => DatabaseType.Float64,
            "DECIMAL" or "NUMERIC" => DatabaseType.Decimal,
            "CHAR" or "CHARACTER" or "VARCHAR" or "TEXT" => DatabaseType.String,
            "BINARY" or "VARBINARY" or "BLOB" or "BYTEA" => DatabaseType.Binary,
            "DATE" => DatabaseType.Date,
            "TIME" => DatabaseType.Time,
            "TIMESTAMP" or "DATETIME" => DatabaseType.DateTime,
            "TIMESTAMPTZ" => DatabaseType.DateTimeOffset,
            "INTERVAL" => DatabaseType.TimeSpan,
            "UUID" or "GUID" => DatabaseType.Guid,
            "JSON" => DatabaseType.Json,
            "JSONB" => DatabaseType.JsonBinary,
            _ => null,
        };

        if (type is null)
        {
            typeInfo = null!;
            return false;
        }

        // DECIMAL(p) / DECIMAL(p, s): a single argument is precision, not length.
        if (type == DatabaseType.Decimal && length is not null && precision is null)
        {
            precision = length;
            length = null;
        }

        typeInfo = new DatabaseTypeInfo(type.Value, length, precision, scale);
        return true;
    }

    /// <summary>
    /// Resolves a SQL type name without arguments.
    /// </summary>
    /// <param name="name">The SQL type name, case-insensitive.</param>
    /// <param name="type">The resolved shared type identity.</param>
    /// <returns>True when the name is part of the declared dialect; otherwise false.</returns>
    public static bool TryResolve(string name, out DatabaseType type)
    {
        if (TryResolve(name, null, null, null, out var info))
        {
            type = info.Type;
            return true;
        }

        type = DatabaseType.Null;
        return false;
    }
}
