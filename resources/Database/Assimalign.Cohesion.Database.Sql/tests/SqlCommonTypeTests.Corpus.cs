using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: recorded PostgreSQL contexts cover columns, windows, groups and aggregate inputs (#1788)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ContextCorpus_ShouldMatchPostgres17(bool tcp)
    {
        // Arrange: cells were recorded as text in PostgreSQL, preserving numeric digits.
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "resources", "Database"))) { root = Directory.GetParent(root)!.FullName; }
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "resources/Database/Assimalign.Cohesion.Database.Sql/tests/TestObjects/CaseCoalesceContextsPostgres17.json")));
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var setup in corpus.RootElement.GetProperty("setup").EnumerateArray()) { await Execute(session, setup.GetString()!); }
        await using var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync();
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "context-corpus", EndPoint = listener.EndPoint },
            ConnectionFactory = new TcpConnectionFactory(),
        });
        await using var connection = await client.ConnectAsync();
        var differences = new List<string>();
        var distinctKeys = new Dictionary<int, (DatabaseType Type, object?[][] Rows)>();
        int comparisons = 0, departures = 0;

        // Act
        foreach (var item in cases)
        {
            string sql = item.GetProperty("sql").GetString()!, shape = item.GetProperty("shape").GetString()!;
            string? state = item.GetProperty("state").GetString();
            try
            {
                var actual = await Pass2Observe(session, tcp ? connection : null, sql);
                if (shape == "distinct") { distinctKeys[item.GetProperty("source").GetInt32()] = (actual.Types[0], actual.Rows); }
                if (shape == "key")
                {
                    var distinct = distinctKeys[item.GetProperty("source").GetInt32()];
                    if (actual.Types[0] != distinct.Type || !actual.Rows.Select(row => row[0]).SequenceEqual(distinct.Rows.Select(row => row[0])))
                    { differences.Add(sql + ": GROUP BY must agree with DISTINCT on key values and metadata"); }
                }
                if (state is not null || shape == "array-aggregate") { differences.Add(sql + ": unexpected acceptance"); continue; }
                var expectedTypes = item.GetProperty("types").EnumerateArray().Select(type => OracleType(type.GetString()!)).ToArray();
                var expected = item.GetProperty("vals").EnumerateArray().Select(row => row.EnumerateArray()
                    .Select((cell, index) => cell.ValueKind == JsonValueKind.Null ? null : OracleValue(expectedTypes[index], cell.GetString()!)).ToArray()).ToArray();
                var postgresTypes = expectedTypes.ToArray();
                var postgresRows = expected.Select(row => row.ToArray()).ToArray();
                bool departure = false;
                // DIALECT: SUM/AVG accumulate Decimal, including REAL/DOUBLE's existing
                // CLR conversion loss. Derive the answer from independently recorded inputs.
                if (shape is "aggregate-SUM" or "aggregate-AVG" or "window-input")
                {
                    var source = cases[item.GetProperty("source").GetInt32()];
                    var sourceType = OracleType(source.GetProperty("types")[0].GetString()!);
                    var inputs = source.GetProperty("vals").EnumerateArray().Select(row => row[0].ValueKind == JsonValueKind.Null
                        ? (decimal?)null : Convert.ToDecimal(OracleValue(sourceType, row[0].GetString()!), CultureInfo.InvariantCulture)).ToArray();
                    decimal sum = 0;
                    int count = 0;
                    var totals = new List<object?[]>();
                    foreach (var input in inputs)
                    {
                        if (input is { } value) { sum += value; count++; }
                        totals.Add([count == 0 ? null : sum]);
                    }
                    expectedTypes = [DatabaseType.Decimal];
                    expected = shape == "window-input" ? totals.ToArray() : [[count == 0 ? null : shape == "aggregate-AVG" ? sum / count : sum]];
                    departure = true;
                }
                // DIALECT: grouped output ranks Decimal above Double above Real and
                // folds CHAR/TEXT to TEXT. A key slot itself keeps its scalar type.
                if (shape == "grouped-output")
                {
                    if (expectedTypes[0] is DatabaseType.Character or DatabaseType.String)
                    { expectedTypes[0] = DatabaseType.String; expected[0][0] = ((string?)expected[0][0])?.TrimEnd(' '); }
                    else { expectedTypes[0] = DatabaseType.Decimal; expected[0][0] = Convert.ToDecimal(expected[0][0], CultureInfo.InvariantCulture); }
                    departure = true;
                }
                // DIALECT: the shared CHAR/TEXT and CHAR/VARCHAR identity is TEXT in
                // either order, unlike PostgreSQL's first non-TEXT candidate.
                if (sql.Contains("COALESCE(c4,txt)", StringComparison.Ordinal) && expectedTypes[0] == DatabaseType.Character)
                {
                    expectedTypes[0] = DatabaseType.String;
                    foreach (var row in expected) { row[0] = ((string?)row[0])?.TrimEnd(' '); }
                    departure = true;
                }
                bool equal = actual.Types.SequenceEqual(expectedTypes) && actual.Rows.Length == expected.Length;
                if (equal)
                {
                    for (int row = 0; row < expected.Length; row++)
                    {
                        for (int column = 0; column < expectedTypes.Length; column++)
                        {
                            object? a = actual.Rows[row][column], b = expected[row][column];
                            bool cellEqual = Equals(a, b);
                            // The documented statistical transitions retain Decimal's
                            // finite precision and PostgreSQL's floating algorithm.
                            if (!cellEqual && shape == "statistics")
                            {
                                cellEqual = a is decimal d && b is decimal e
                                        && decimal.Round(d, (decimal.GetBits(e)[3] >> 16) & 0xff, MidpointRounding.AwayFromZero) == e
                                    || a is double f && b is double g && Math.Abs(f - g) <= Math.Max(1, Math.Abs(g)) * 1e-12;
                                if (cellEqual) { departure = true; }
                            }
                            if (!cellEqual) { equal = false; }
                        }
                    }
                }
                if (!equal) { differences.Add($"{item.GetProperty("id")}: {sql}; PG/contract {string.Join(',', expectedTypes)} [{Cells(expected)}]; actual {string.Join(',', actual.Types)} [{Cells(actual.Rows)}]"); }
                else if (departure && (!actual.Types.SequenceEqual(postgresTypes) || !SameRows(actual.Rows, postgresRows))) { departures++; }
            }
            catch (Exception failure) when (failure is DatabaseException or SqlClientException)
            {
                string? code = failure is DatabaseException local ? local.Diagnostics[0].Code : ((SqlClientException)failure).Diagnostics[0].Code;
                if (shape == "array-aggregate" && code == "CS01016") { departures++; }
                else if (state != "22P02" || code != "CS02022") { differences.Add(sql + ": " + code + " " + failure.Message); }
            }
            comparisons++;
        }

        // Assert
        foreach (string difference in differences) { _output.WriteLine(difference); }
        _output.WriteLine($"PostgreSQL contexts={comparisons}; recorded departures={departures}; defects={differences.Count}; TCP={tcp}");
        comparisons.ShouldBe(137);
        departures.ShouldBe(37);
        differences.ShouldBeEmpty();

        static string Cells(object?[][] rows) => string.Join(';', rows.Select(row => string.Join('|', row.Select(cell => Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "NULL"))));
        static bool SameRows(object?[][] first, object?[][] second)
            => first.Length == second.Length && first.Select((row, index) => row.SequenceEqual(second[index])).All(equal => equal);
    }
}
