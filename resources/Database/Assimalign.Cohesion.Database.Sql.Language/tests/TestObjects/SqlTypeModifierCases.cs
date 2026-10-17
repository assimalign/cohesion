using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

using Assimalign.Cohesion.Database.Types;

// Test-only linked source: one boundary corpus exercises independent declaration entry points.
namespace Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;

public static class SqlTypeModifierCases
{
    public static IEnumerable<object[]> Types => All.Select(row => new[] { row[0], row[1] });
    public static IEnumerable<object[]> Sql => All.Select(row => new[] { row[0], row[1], row[2] });
    public static IEnumerable<object[]> Columns => All.Select(row => new[] { row[0], row[1], row[3] });
    public static IEnumerable<object[]> UnsupportedTemporalStatements => UnsupportedTemporal.SelectMany(row =>
        Statements((string)row[0]).Append(($"DROP FUNCTION parameter_function({row[0]})", "parameter type"))
            .Select(statement => new object[] { row[0], statement.Item1, statement.Item2 }));

    public static IEnumerable<object[]> NationalCharacters
    {
        get
        {
            foreach (string name in new[] { "NATIONAL CHAR", "NATIONAL CHARACTER", "NCHAR" })
            {
                yield return [name, DatabaseType.Character, 1];
                yield return [name + "(10)", DatabaseType.Character, 10];
                yield return [name + " VARYING", DatabaseType.String, null!];
                yield return [name + " VARYING(10)", DatabaseType.String, 10];
            }
        }
    }

    public static IEnumerable<object[]> Floating
    {
        get
        {
            yield return ["FLOAT", DatabaseType.Float64];
            yield return ["FLOAT(1)", DatabaseType.Float32];
            yield return ["FLOAT(24)", DatabaseType.Float32];
            yield return ["FLOAT(25)", DatabaseType.Float64];
            yield return ["FLOAT(53)", DatabaseType.Float64];
        }
    }

    public static IEnumerable<object[]> UnsupportedTemporal
    {
        get
        {
            foreach (var (name, member) in new[] { ("TIME", "Time"), ("TIMESTAMP", "Timestamp"),
                ("TIMESTAMPTZ", "TimestampTz"), ("INTERVAL", "Duration"), ("DATETIME", "Timestamp"),
                ("DATETIME2", "Timestamp"), ("DATETIMEOFFSET", "TimestampTz"),
                ("TIMESTAMP WITH TIME ZONE", "TimestampTz"), ("TIMESTAMP WITHOUT TIME ZONE", "Timestamp"),
                ("TIME WITHOUT TIME ZONE", "Time") })
            {
                foreach (int precision in new[] { 0, 2, 3, 6, 7, int.MaxValue })
                {
                    yield return [$"{name}({precision})", member];
                }
            }
            // PostgreSQL gram.y:15197-15267: every field/range, plus SECOND precision.
            foreach (string qualifier in new[] { "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND",
                "YEAR TO MONTH", "DAY TO HOUR", "DAY TO MINUTE", "DAY TO SECOND",
                "HOUR TO MINUTE", "HOUR TO SECOND", "MINUTE TO SECOND" })
            {
                yield return [$"INTERVAL {qualifier}", "Duration"];
                if (qualifier.EndsWith("SECOND", StringComparison.Ordinal))
                {
                    foreach (int precision in new[] { 0, 3, 6, 7, int.MaxValue })
                    {
                        yield return [$"INTERVAL {qualifier}({precision})", "Duration"];
                    }
                }
            }
        }
    }

    public static IEnumerable<object[]> All
    {
        get
        {
            foreach (string name in new[] { "NUMERIC", "DECIMAL" })
            {
                foreach (var (suffix, valid) in new (string, bool)[]
                {
                    ("", true), ("(0)", false), ("(1)", true), ("(28)", true), ("(29)", false),
                    ("(40,2)", false), ("(1,0)", true), ("(1,1)", true), ("(1,2)", false),
                    ("(28,0)", true), ("(28,28)", true), ("(28,29)", false), ("(28,-1)", false),
                    ("(2147483647)", false), ("(2147483648)", false), ("(1,0,0)", false),
                })
                {
                    yield return [name + suffix, valid, "NUMERIC", "Amount"];
                }
            }
            foreach (string name in new[] { "VARCHAR", "CHAR", "CHARACTER", "TEXT", "BINARY", "VARBINARY", "BLOB", "BYTEA" })
            {
                bool binary = name is "BINARY" or "VARBINARY" or "BLOB" or "BYTEA";
                foreach (var (suffix, valid) in new (string, bool)[]
                {
                    ("", true), ("(0)", false), ("(1)", true), ("(10485760)", true), ("(10485761)", false),
                    ("(2147483647)", false), ("(2147483648)", false), ("(1,1)", false),
                    ("(-1)", false), ("()", false),
                })
                {
                    yield return [name + suffix, valid, binary ? "BINARY" : "TEXT", binary ? "Bytes" : "Text"];
                }
            }
            foreach (string type in new[] { "DOUBLE PRECISION", "CHARACTER VARYING(20)", "CHAR VARYING(20)",
                "NATIONAL CHARACTER VARYING(20)", "NVARCHAR(20)", "VARCHAR(MAX)", "NVARCHAR(MAX)",
                "BINARY VARYING(20)", "VARBINARY(MAX)" })
            {
                bool binary = type.StartsWith("BINARY", StringComparison.Ordinal) || type.StartsWith("VARBINARY", StringComparison.Ordinal);
                bool floating = type == "DOUBLE PRECISION";
                yield return [type, true, floating ? "DOUBLE" : binary ? "BINARY" : "TEXT", floating ? "Double" : binary ? "Bytes" : "Text"];
            }
            foreach (object[] national in NationalCharacters)
            {
                yield return [national[0], true, "TEXT", "Text"];
            }
            yield return ["INT", true, "INT", "Number"];
            yield return ["INT(5)", false, "INT", "Number"];
            yield return ["BOOLEAN", true, "BOOLEAN", "Flag"];
            yield return ["BOOLEAN(1)", false, "BOOLEAN", "Flag"];
            foreach (object[] floating in Floating)
            {
                bool single = (DatabaseType)floating[1] == DatabaseType.Float32;
                yield return [floating[0], true, single ? "REAL" : "DOUBLE", single ? "Single" : "Double"];
            }
            foreach (string type in new[] { "FLOAT(0)", "FLOAT(54)", "FLOAT(2147483647)", "FLOAT(2147483648)",
                "FLOAT(-1)", "FLOAT()", "FLOAT(24,1)", "REAL(24)", "DOUBLE(53)", "FLOAT4(24)", "FLOAT8(53)" })
            {
                yield return [type, false, "DOUBLE", "Double"];
            }
            foreach (var (name, member) in new[] { ("TIME", "Time"), ("TIMESTAMP", "Timestamp"),
                ("TIMESTAMPTZ", "TimestampTz"), ("INTERVAL", "Duration"), ("DATETIME", "Timestamp") })
            {
                yield return [name, true, name, member];
                foreach (string suffix in new[] { "(-1)", "()", "(1,2)", "(2147483648)" })
                {
                    yield return [name + suffix, false, name, member];
                }
            }
            foreach (string type in new[] { "INTERVAL SECOND(-1)", "INTERVAL SECOND()", "INTERVAL SECOND(2147483648)",
                "INTERVAL SECOND(1,2)", "INTERVAL SECOND(foo)", "INTERVAL DAY TO SECOND(1,2)",
                "INTERVAL DAY(3)", "INTERVAL DAY TO HOUR(3)" })
            {
                yield return [type, false, "INTERVAL", "Duration"];
            }
        }
    }

    public static IEnumerable<(string Sql, string Subject)> Statements(string type)
    {
        yield return ($"CREATE TABLE created (value {type})", "Column 'value'");
        yield return ($"ALTER TABLE added ADD COLUMN value {type}", "Column 'value'");
        yield return ($"ALTER TABLE altered ALTER COLUMN value {type}", "Column 'value'");
        yield return ($"CREATE DOMAIN bounded AS {type}", "Domain 'bounded'");
        yield return ($"CREATE FUNCTION parameter_function(value {type}) RETURNS INT RETURN 1", "parameter 'value'");
        yield return ($"CREATE FUNCTION result_function() RETURNS {type} RETURN NULL", "return type");
        yield return ($"SELECT CAST(NULL AS {type}) FROM added", "CAST target");
    }

    public static Expression<Func<Row, object?>> Selector(string member) => member switch
    {
        "Amount" => row => row.Amount,
        "Text" => row => row.Text,
        "Bytes" => row => row.Bytes,
        "Number" => row => row.Number,
        "Flag" => row => row.Flag,
        "Single" => row => row.Single,
        "Double" => row => row.Double,
        "Time" => row => row.Time,
        "Timestamp" => row => row.Timestamp,
        "TimestampTz" => row => row.TimestampTz,
        "Duration" => row => row.Duration,
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };

    public sealed class Row
    {
        public decimal Amount { get; set; }
        public string Text { get; set; } = string.Empty;
        public byte[] Bytes { get; set; } = [];
        public int Number { get; set; }
        public bool Flag { get; set; }
        public float Single { get; set; }
        public double Double { get; set; }
        public TimeOnly Time { get; set; }
        public DateTime Timestamp { get; set; }
        public DateTimeOffset TimestampTz { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
