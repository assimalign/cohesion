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
            "FLOAT" or "FLOAT8" or "DOUBLE" or "DOUBLE PRECISION" => DatabaseType.Float64,
            "DECIMAL" or "NUMERIC" => DatabaseType.Decimal,
            "CHAR" or "CHARACTER" or "NCHAR" or "NATIONAL CHARACTER" => DatabaseType.Character,
            "VARCHAR" or "TEXT" or "NVARCHAR" or "CHARACTER VARYING" or "CHAR VARYING" or "NATIONAL CHARACTER VARYING" => DatabaseType.String,
            "BINARY" or "VARBINARY" or "BLOB" or "BYTEA" or "BINARY VARYING" => DatabaseType.Binary,
            "DATE" => DatabaseType.Date,
            "TIME" or "TIME WITHOUT TIME ZONE" => DatabaseType.Time,
            "TIMESTAMP" or "DATETIME" or "DATETIME2" or "TIMESTAMP WITHOUT TIME ZONE" => DatabaseType.DateTime,
            "TIMESTAMPTZ" or "DATETIMEOFFSET" or "TIMESTAMP WITH TIME ZONE" => DatabaseType.DateTimeOffset,
            "INTERVAL" => DatabaseType.TimeSpan,
            "UUID" or "GUID" or "UNIQUEIDENTIFIER" => DatabaseType.Guid,
            "JSON" => DatabaseType.Json,
            "JSONB" => DatabaseType.JsonBinary,
            _ => null,
        };

    internal static bool AllowsMax(string name)
        => name.ToUpperInvariant() is "VARCHAR" or "NVARCHAR" or "VARBINARY";

    internal static string? Rejection(string name) => name.ToUpperInvariant() switch
    {
        "TIME WITH TIME ZONE" => "TIME WITH TIME ZONE has no shared storage type; use TIME WITHOUT TIME ZONE or TIMESTAMP WITH TIME ZONE.",
        "BIT" => "BIT denotes PostgreSQL bit strings, which are not supported; use BOOLEAN for a truth value.",
        "SMALLDATETIME" => "SMALLDATETIME range and minute precision cannot be honored; use TIMESTAMP.",
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

    // Typed schemas recognize interval qualifiers to diagnose them, but never emit them as DDL.
    // Other typed names are identifiers or entries of the closed built-in name table;
    // SQL separately supports delimiters.
    internal static bool IsTypeName(string text)
    {
        int open = text.IndexOf('(');
        string name = open < 0 ? text : text[..open].TrimEnd();
        return (IsIdentifier(name) || StorageType(name) is not null || Rejection(name) is not null || IsIntervalQualifiedName(name)) &&
            (open < 0 || TrySplit(text, out _, out _, out _));
    }

    internal static bool IsIntervalField(string name)
        => name.ToUpperInvariant() is "YEAR" or "MONTH" or "DAY" or "HOUR" or "MINUTE" or "SECOND";

    internal static bool IsIntervalRange(string first, string last)
        => (first.ToUpperInvariant(), last.ToUpperInvariant()) is
            ("YEAR", "MONTH") or ("DAY", "HOUR" or "MINUTE" or "SECOND") or
            ("HOUR", "MINUTE" or "SECOND") or ("MINUTE", "SECOND");

    private static bool IsIntervalQualifiedName(string name)
    {
        if (name.Length <= 8 || !name.AsSpan(0, 8).Equals("INTERVAL", StringComparison.OrdinalIgnoreCase) ||
            !char.IsWhiteSpace(name[8]))
        {
            return false;
        }
        // PostgreSQL src/backend/parser/gram.y:15197-15267, opt_interval/interval_second.
        string[] words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 2 && IsIntervalField(words[1]) ||
            words.Length == 4 && words[2].Equals("TO", StringComparison.OrdinalIgnoreCase) &&
            IsIntervalRange(words[1], words[3]);
    }

    internal static string? GetError(string name, int? first, int? second, bool isDelimited = false)
        => GetError(name, first, second, out string code, isDelimited) is { } error ? $"{code}: {error}" : null;

    internal static string? GetError(string name, int? first, int? second, out string code, bool isDelimited = false)
    {
        code = DiagnosticCode;
        if (!isDelimited && Rejection(name) is { } rejection) { return rejection; }
        if (!isDelimited && IsIntervalQualifiedName(name))
        {
            if (second is not null || first < 0 ||
                first is not null && !name.TrimEnd().EndsWith("SECOND", StringComparison.OrdinalIgnoreCase))
            {
                return "Only the SECOND interval field accepts one nonnegative precision argument.";
            }
            code = "COHDBL001";
            string modifier = first is null ? name : string.Create(CultureInfo.InvariantCulture, $"{name}({first})");
            return $"Interval field modifier '{modifier}' is recognized but not supported by the SQL model surface; field restrictions are not enforced, so use bare INTERVAL.";
        }
        DatabaseType? type = StorageType(name);
        if (string.Equals(name, "FLOAT", StringComparison.OrdinalIgnoreCase))
        {
            // PostgreSQL src/backend/parser/gram.y:14991-15016: precision selects storage.
            if (second is not null) { return "FLOAT accepts one precision argument in bits."; }
            if (first is < 1) { return "Precision for type float must be at least 1 bit."; }
            if (first is > 53) { return "Precision for type float must be less than 54 bits."; }
            return null;
        }
        if (type is DatabaseType.Time or DatabaseType.DateTime or DatabaseType.DateTimeOffset or DatabaseType.TimeSpan &&
            first is not null)
        {
            if (first < 0 || second is not null)
            {
                return "Temporal types accept one nonnegative precision argument.";
            }
            // PostgreSQL accepts temporal precision and clamps values above six with a warning:
            // date.c:57-81; timestamp.c:110-132,1096-1110. We do not implement that rounding yet.
            code = "COHDBL001";
            return string.Create(CultureInfo.InvariantCulture,
                $"Temporal precision modifier '{name}({first})' is recognized but not supported by the SQL model surface.");
        }
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
        if (type is DatabaseType.String or DatabaseType.Character or DatabaseType.Binary)
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

            if (arguments[..^1].Trim().Equals("MAX", StringComparison.OrdinalIgnoreCase) && AllowsMax(name))
            {
                return true;
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
