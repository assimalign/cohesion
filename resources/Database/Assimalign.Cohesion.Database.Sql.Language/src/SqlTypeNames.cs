using System;

using Assimalign.Cohesion.Database.Sql.Language.Internal;
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
    /// <returns>True when the name and modifiers are valid in the declared dialect; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static bool TryResolve(string name, int? length, int? precision, int? scale, out DatabaseTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(name);

        DatabaseType? type = SqlTypeModifiers.StorageType(name);

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

        if (SqlTypeModifiers.GetError(type, length, precision, scale) is not null)
        {
            typeInfo = null!;
            return false;
        }

        // An explicit precision implies scale zero; a bare DECIMAL remains unconstrained.
        typeInfo = new DatabaseTypeInfo(type.Value, length, precision,
            SqlTypeModifiers.NormalizeScale(type, precision, scale));
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
