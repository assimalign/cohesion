using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    private readonly ITestOutputHelper _output;

    public SqlCommonTypeTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: every shared type pair matches the executed PostgreSQL 17 oracle (#1788)")]
    public async Task Execute_AllTypePairs_ShouldMatchPostgres17()
    {
        // Arrange: captured from PostgreSQL 17.11, not inferred from this implementation.
        string directory = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(directory, "resources", "Database")))
        { directory = Directory.GetParent(directory)!.FullName; }
        string corpus = Path.Combine(directory, "resources/Database/Assimalign.Cohesion.Database.Sql/tests/TestObjects/CaseCoalescePostgres17.tsv");
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var differences = new List<string>();
        int comparisons = 0;
        int characterDepartures = 0;

        // Act: both CASE branches, a present COALESCE argument and a typed NULL argument.
        foreach (string line in File.ReadLines(corpus).Skip(1))
        {
            string[] entry = line.Split('\t');
            string first = Operand(entry[1], entry[3] == "3"), second = Operand(entry[2]);
            string expression = entry[3] switch
            {
                "0" => $"CASE WHEN TRUE THEN {first} ELSE {second} END",
                "1" => $"CASE WHEN FALSE THEN {first} ELSE {second} END",
                _ => $"COALESCE({first}, {second})",
            };
            try
            {
                await using var result = (await session.ExecuteAsync("SELECT " + expression, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
                var rows = new List<object?>();
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row.GetValue(0)); }
                if (entry[6].Length != 0) { differences.Add($"{entry[0]} {expression}: PG {entry[6]}, accepted {result.Columns[0].Type}"); }
                else
                {
                    var type = OracleType(entry[4]);
                    // DIALECT: mixed CHAR/TEXT results use TEXT in either order. PG can
                    // retain bpchar when it is the first candidate (CASE's ELSE first).
                    bool characterDeparture = type == DatabaseType.Character && result.Columns[0].Type == DatabaseType.String
                        && (entry[1] is "TEXT" or "VARCHAR(8)" || entry[2] is "TEXT" or "VARCHAR(8)");
                    if (characterDeparture) { type = DatabaseType.String; characterDepartures++; }
                    object? actual = rows.FirstOrDefault();
                    if (type == DatabaseType.Character && actual is string text) { actual = text.TrimEnd(' '); }
                    object? expected = OracleValue(type, entry[5]);
                    bool equal = actual is byte[] bytes && expected is byte[] expectedBytes ? bytes.SequenceEqual(expectedBytes) : Equals(actual, expected);
                    if (result.Columns[0].Type != type || rows.Count != 1 || !equal)
                    { differences.Add($"{entry[0]} {expression}: PG {entry[4]} [{entry[5]}], actual {result.Columns[0].Type} [{rows.FirstOrDefault()}]"); }
                }
            }
            catch (DatabaseException failure)
            {
                string code = failure.Diagnostics.First().Code!;
                if (entry[6].Length == 0 || (entry[6] is "42804" or "42846") && code != "CS02080" || entry[6] == "22P02" && code != "CS02022")
                { differences.Add($"{entry[0]} {expression}: PG {entry[4]} {entry[6]}, actual {code}: {failure.Message}"); }
            }
            comparisons++;
        }

        // Assert
        foreach (string difference in differences) { _output.WriteLine(difference); }
        _output.WriteLine($"PostgreSQL comparisons={comparisons}, recorded CHAR departures={characterDepartures}, defects={differences.Count}");
        comparisons.ShouldBe(1936);
        characterDepartures.ShouldBe(8);
        differences.ShouldBeEmpty();
    }

    private static string Operand(string type, bool nullValue = false)
    {
        if (type == "null") { return "NULL"; }
        if (type == "unknown") { return "'11'"; }
        if (nullValue) { return $"CAST(NULL AS {type})"; }
        string value = type switch
        {
            "SMALLINT" => "7", "INT" => "8", "BIGINT" => "9", "NUMERIC(6,2)" => "1.25", "NUMERIC(8,3)" => "2.375",
            "REAL" => "3.5", "DOUBLE PRECISION" => "4.75", "TEXT" => "a", "VARCHAR(8)" => "b", "CHAR(4)" => "c",
            "BOOLEAN" => "true", "DATE" => "2001-01-02", "TIME" => "03:04:05", "TIMESTAMP" => "2001-01-02 03:04:05",
            "TIMESTAMPTZ" => "2001-01-02 03:04:05+00:00", "JSON" or "JSONB" => "{}",
            "UUID" => "00000000-0000-0000-0000-000000000001", "INTERVAL" => "01:02:03", _ => "",
        };
        return type == "BINARY" ? "X'0102'" : $"CAST('{value}' AS {type})";
    }

    private static DatabaseType OracleType(string type) => type switch
    {
        "smallint" => DatabaseType.Int16, "integer" => DatabaseType.Int32, "bigint" => DatabaseType.Int64,
        "numeric" => DatabaseType.Decimal, "real" => DatabaseType.Float32, "double precision" => DatabaseType.Float64,
        "text" or "character varying" => DatabaseType.String, "character" => DatabaseType.Character,
        "boolean" => DatabaseType.Boolean, "date" => DatabaseType.Date, "time without time zone" => DatabaseType.Time,
        "timestamp without time zone" => DatabaseType.DateTime, "timestamp with time zone" => DatabaseType.DateTimeOffset,
        "interval" => DatabaseType.TimeSpan, "uuid" => DatabaseType.Guid, "bytea" => DatabaseType.Binary,
        "json" => DatabaseType.Json, "jsonb" => DatabaseType.JsonBinary, _ => throw new InvalidOperationException(type),
    };

    private static object? OracleValue(DatabaseType type, string value)
    {
        if (value == "<NULL>") { return null; }
        var culture = CultureInfo.InvariantCulture;
        return type switch
        {
            DatabaseType.Int16 => short.Parse(value, culture), DatabaseType.Int32 => int.Parse(value, culture), DatabaseType.Int64 => long.Parse(value, culture),
            DatabaseType.Decimal => decimal.Parse(value, culture), DatabaseType.Float32 => float.Parse(value, culture), DatabaseType.Float64 => double.Parse(value, culture),
            DatabaseType.Boolean => value == "true", DatabaseType.Date => DateOnly.Parse(value, culture), DatabaseType.Time => TimeOnly.Parse(value, culture),
            DatabaseType.DateTime => DateTime.Parse(value, culture), DatabaseType.DateTimeOffset => DateTimeOffset.Parse(value, culture),
            DatabaseType.TimeSpan => TimeSpan.Parse(value, culture), DatabaseType.Guid => Guid.Parse(value),
            DatabaseType.Binary => Convert.FromHexString(value[2..]), DatabaseType.JsonBinary => System.Text.Encoding.UTF8.GetBytes(value),
            _ => value,
        };
    }
}
