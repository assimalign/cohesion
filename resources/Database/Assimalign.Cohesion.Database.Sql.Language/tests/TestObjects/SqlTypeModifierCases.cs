using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

// Test-only linked source: one boundary corpus exercises independent declaration entry points.
namespace Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;

public static class SqlTypeModifierCases
{
    public static IEnumerable<object[]> Types => All.Select(row => new[] { row[0], row[1] });
    public static IEnumerable<object[]> Sql => All.Select(row => new[] { row[0], row[1], row[2] });
    public static IEnumerable<object[]> Columns => All.Select(row => new[] { row[0], row[1], row[3] });

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
            yield return ["INT", true, "INT", "Number"];
            yield return ["INT(5)", false, "INT", "Number"];
            yield return ["BOOLEAN", true, "BOOLEAN", "Flag"];
            yield return ["BOOLEAN(1)", false, "BOOLEAN", "Flag"];
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
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };

    public sealed class Row
    {
        public decimal Amount { get; set; }
        public string Text { get; set; } = string.Empty;
        public byte[] Bytes { get; set; } = [];
        public int Number { get; set; }
        public bool Flag { get; set; }
    }
}
