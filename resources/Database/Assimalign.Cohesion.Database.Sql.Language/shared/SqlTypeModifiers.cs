using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Types;

// Deviates from the repo RootNamespace rule per shared-source design: stateless SQL type rules
// keep the owning language namespace when compiled into SQL, schema and SDK assemblies.
namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>Declaration-time modifier rules, shared by SQL syntax, resolution and typed schemas.</summary>
internal static class SqlTypeModifiers
{
    internal const string DiagnosticCode = "SQL0005";
    internal const int MaximumLength = 10_485_760;
    internal const int MaximumPrecision = 28;
    internal const string ArgumentShape = "Type arguments must be one or two unsigned integer literals within the Int32 range.";

    // A bare decimal is unconstrained; an explicit precision implies scale zero everywhere.
    internal static int? NormalizeScale(DatabaseType? type, int? precision, int? scale)
        => type == DatabaseType.Decimal && precision is not null ? scale ?? 0 : scale;

    // PostgreSQL validates declaration lengths in src/backend/utils/adt/varchar.c:33-57;
    // MaxAttrSize is 10 Mi characters (src/include/access/htup_details.h:620-626). Binary uses
    // the same explicit ceiling here. Omitted bounds stay unbounded; a bound allocates nothing.
    internal static DatabaseType? StorageType(string name)
        => name.ToUpperInvariant() switch
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

    internal static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || !(char.IsAsciiLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }
        foreach (char character in text)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }
        return true;
    }

    // Typed schemas retain their identifier-only spelling; SQL separately supports delimiters.
    internal static bool IsTypeName(string text)
    {
        int open = text.IndexOf('(');
        return open < 0 ? IsIdentifier(text) :
            IsIdentifier(text.AsSpan(0, open).TrimEnd()) && TrySplit(text, out _, out _, out _);
    }

    internal static string? GetError(string name, int? first, int? second)
    {
        DatabaseType? type = StorageType(name);
        return type == DatabaseType.Decimal
            ? GetError(type, null, first, second)
            : GetError(type, first, null, second);
    }

    internal static string? GetError(DatabaseType? type, int? length, int? precision, int? scale)
    {
        scale = NormalizeScale(type, precision, scale);
        if (type == DatabaseType.Decimal)
        {
            if (length is not null)
            {
                return "Decimal types accept precision and scale, not a length.";
            }
            if (precision is < 1 or > MaximumPrecision)
            {
                return "The decimal precision must be between 1 and 28.";
            }
            if (scale is not null && (precision is null || scale < 0 || scale > precision))
            {
                return "The decimal scale must be between 0 and precision.";
            }
            return null;
        }
        if (type is DatabaseType.String or DatabaseType.Binary)
        {
            return length is < 1 or > MaximumLength || precision is not null || scale is not null
                ? "String and binary types accept one positive length argument between 1 and 10485760."
                : null;
        }
        return length is not null || precision is not null || scale is not null
            ? "This type does not accept type arguments."
            : null;
    }

    internal static bool TrySplit(string text, out string name, out int? first, out int? second)
    {
        name = text.Trim();
        first = null;
        second = null;
        int open;
        if (name.StartsWith('"'))
        {
            int end = 1;
            while (end < name.Length)
            {
                if (name[end] != '"') { end++; continue; }
                if (end + 1 < name.Length && name[end + 1] == '"') { end += 2; continue; }
                break;
            }
            if (end == name.Length) { return false; }
            string quotedName = name[1..end].Replace("\"\"", "\"", StringComparison.Ordinal);
            string remainder = name[(end + 1)..].TrimStart();
            if (remainder.Length == 0)
            {
                name = quotedName;
                return name.Length > 0;
            }
            if (remainder[0] != '(') { return false; }
            name = quotedName + remainder;
            open = quotedName.Length;
        }
        else
        {
            open = name.IndexOf('(');
        }
        if (open >= 0)
        {
            string arguments = name[(open + 1)..];
            name = name[..open].TrimEnd();
            if (!arguments.EndsWith(')'))
            {
                return false;
            }

            var parts = arguments[..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length > 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedFirst))
            {
                return false;
            }

            first = parsedFirst;
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSecond))
                {
                    return false;
                }

                second = parsedSecond;
            }
        }

        return name.Length > 0;
    }

}
