using System;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The executable OQL function vocabulary, arities, kinds and static result types.</summary>
public static class OqlFunctions
{
    private static readonly IReadOnlyList<OqlFunction> _all = Array.AsReadOnly<OqlFunction>(
    [
        new("META", 0, 1, DatabaseType.Json),
        new("COUNT", 1, 1, DatabaseType.Int64, true),
        new("SUM", 1, 1, DatabaseType.Decimal, true),
        new("AVG", 1, 1, DatabaseType.Decimal, true),
        new("MIN", 1, 1, DatabaseType.Null, true),
        new("MAX", 1, 1, DatabaseType.Null, true),
        new("LOWER", 1, 1, DatabaseType.String, false, JsonValueKind.String),
        new("UPPER", 1, 1, DatabaseType.String, false, JsonValueKind.String),
        new("LENGTH", 1, 1, DatabaseType.Int64, false, JsonValueKind.String),
        new("SUBSTR", 2, 3, DatabaseType.String, false, JsonValueKind.String, JsonValueKind.Number),
        new("POSITION", 2, 2, DatabaseType.Int64, false, JsonValueKind.String),
        new("CONTAINS", 2, 2, DatabaseType.Boolean, false, JsonValueKind.String),
        new("STARTS_WITH", 2, 2, DatabaseType.Boolean, false, JsonValueKind.String),
        new("ENDS_WITH", 2, 2, DatabaseType.Boolean, false, JsonValueKind.String),
        new("REPLACE", 3, 3, DatabaseType.String, false, JsonValueKind.String),
        new("TRIM", 1, 2, DatabaseType.String, false, JsonValueKind.String),
        new("CONCAT", 2, int.MaxValue, DatabaseType.String, false, JsonValueKind.String),
        new("ABS", 1, 1, DatabaseType.Decimal, false, JsonValueKind.Number),
        new("ROUND", 1, 2, DatabaseType.Decimal, false, JsonValueKind.Number),
        new("TRUNC", 1, 2, DatabaseType.Decimal, false, JsonValueKind.Number),
        new("FLOOR", 1, 1, DatabaseType.Decimal, false, JsonValueKind.Number),
        new("CEIL", 1, 1, DatabaseType.Decimal, false, JsonValueKind.Number),
        new("POWER", 2, 2, DatabaseType.Decimal, false, JsonValueKind.Number),
        new("SQRT", 1, 1, DatabaseType.Decimal, false, JsonValueKind.Number),
        new("NOW_STR", 0, 0, DatabaseType.String),
        new("NOW_MILLIS", 0, 0, DatabaseType.Int64),
        new("STR_TO_MILLIS", 1, 1, DatabaseType.Int64, false, JsonValueKind.String),
        new("MILLIS_TO_STR", 1, 1, DatabaseType.String, false, JsonValueKind.Number),
        new("TYPE", 1, 1, DatabaseType.String),
        new("IS_ARRAY", 1, 1, DatabaseType.Boolean),
        new("IS_OBJECT", 1, 1, DatabaseType.Boolean),
        new("IS_STRING", 1, 1, DatabaseType.Boolean),
        new("IS_NUMBER", 1, 1, DatabaseType.Boolean),
        new("IS_BOOLEAN", 1, 1, DatabaseType.Boolean),
        new("ARRAY_LENGTH", 1, 1, DatabaseType.Int64, false, JsonValueKind.Array),
        new("ARRAY_CONTAINS", 2, 2, DatabaseType.Boolean, false, JsonValueKind.Array, JsonValueKind.Undefined),
        new("COALESCE", 2, int.MaxValue, DatabaseType.Null),
        new("IFMISSING", 2, int.MaxValue, DatabaseType.Null),
        new("IFNULL", 2, int.MaxValue, DatabaseType.Null),
        new("IFMISSINGORNULL", 2, int.MaxValue, DatabaseType.Null),
    ]);

    /// <summary>Gets the immutable function table in declaration order.</summary>
    public static IReadOnlyList<OqlFunction> All => _all;

    /// <summary>Finds an executable function by its case-insensitive name.</summary>
    /// <param name="name">The function name.</param>
    /// <returns>The definition, or null for an unknown function.</returns>
    public static OqlFunction? Find(string name)
    {
        foreach (var function in _all)
        {
            if (string.Equals(function.Name, name, StringComparison.OrdinalIgnoreCase)) { return function; }
        }
        return null;
    }
}
