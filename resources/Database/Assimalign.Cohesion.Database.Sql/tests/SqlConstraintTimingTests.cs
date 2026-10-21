using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Guards self-FK validation at 50,000 rows against quadratic batch searches (#1197).</summary>
/// <remarks>
/// Equal row counts are compared: five 10,000-row operations against one 50,000-row operation.
/// Each block uses process CPU time, excluding setup and verification, and the minimum of three
/// rounds after warm-up. Linear work has a ratio near one; a quadratic batch walk approaches five.
/// The generous bound allows index height, allocation and collection costs. Timing tests run alone.
/// </remarks>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlConstraintTimingTests
{
    private const int smallRows = 10_000;
    private const int largeRows = 50_000;
    private const int smallPerBlock = largeRows / smallRows;
    private const int rounds = 3;
    private readonly ITestOutputHelper _output;

    public SqlConstraintTimingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Self FK: 50,000-row DDL and INSERT validation avoids quadratic work (#1197)")]
    [InlineData("constraint")]
    [InlineData("column")]
    [InlineData("insert")]
    public async Task ExecuteAsync_LargeSelfReferencingBatch_ShouldScaleWithRows(string operation)
    {
        // Arrange: all references target later batch rows; ADD COLUMN targets the final key,
        // so a linear search repeated per candidate cannot pass by matching the first row.
        await using var engine = SqlDatabaseEngine.Create("self-fk-scaling", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE digit (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO digit VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)", cancellationToken: CancellationToken.None);
        double small = double.MaxValue;
        double large = double.MaxValue;

        // Act: alternate which block goes first to reduce ordering bias.
        for (int round = 0; round <= rounds; round++)
        {
            double roundSmall;
            double roundLarge;
            if (round % 2 == 0)
            {
                roundSmall = await MeasureBlockAsync(session, operation, smallRows, smallPerBlock);
                roundLarge = await MeasureBlockAsync(session, operation, largeRows, 1);
            }
            else
            {
                roundLarge = await MeasureBlockAsync(session, operation, largeRows, 1);
                roundSmall = await MeasureBlockAsync(session, operation, smallRows, smallPerBlock);
            }
            _output.WriteLine($"{operation} round {round}: small={roundSmall:F3} ms; large={roundLarge:F3} ms");
            if (round > 0)
            {
                small = Math.Min(small, roundSmall);
                large = Math.Min(large, roundLarge);
            }
        }

        // Assert
        _output.WriteLine($"{operation} minimum of {rounds}: small={small:F3} ms; large={large:F3} ms; ratio={large / small:F3}");
        small.ShouldBeGreaterThan(0);
        (large / small).ShouldBeLessThan(2.8, $"{operation}: {small:F3} ms for five {smallRows:N0}-row operations; {large:F3} ms for one {largeRows:N0}-row operation");
    }

    private static async Task<double> MeasureBlockAsync(SqlDatabaseSession session, string operation, int count, int repeats)
    {
        for (int i = 0; i < repeats; i++)
        {
            string table = $"batch_{i}";
            string foreignKey = operation == "insert" ? ", FOREIGN KEY(pid) REFERENCES " + table + "(id)" : "";
            await session.ExecuteAsync($"CREATE TABLE {table} (id INT PRIMARY KEY, pid INT{foreignKey})", cancellationToken: TestTimeout.Token(120));
            if (operation != "insert")
            {
                await session.ExecuteAsync(Insert(table, count), cancellationToken: TestTimeout.Token(120));
            }
        }

        var affected = new long[repeats];
        string[] statements = Enumerable.Range(0, repeats).Select(i => operation switch
        {
            "constraint" => $"ALTER TABLE batch_{i} ADD CONSTRAINT fk_self FOREIGN KEY(pid) REFERENCES batch_{i}(id)",
            "column" => $"ALTER TABLE batch_{i} ADD COLUMN anchor INT DEFAULT {count.ToString(CultureInfo.InvariantCulture)} REFERENCES batch_{i}(id)",
            _ => Insert($"batch_{i}", count),
        }).ToArray();
        TimeSpan start = Environment.CpuUsage.TotalTime;
        for (int i = 0; i < repeats; i++)
        {
            affected[i] = (await session.ExecuteAsync(statements[i], cancellationToken: TestTimeout.Token(120))).AffectedCount;
        }
        double elapsed = (Environment.CpuUsage.TotalTime - start).TotalMilliseconds;

        affected.ShouldAllBe(value => value == (operation == "insert" ? count : 0));
        for (int i = 0; i < repeats; i++)
        {
            await using var result = (await session.ExecuteAsync($"SELECT COUNT(*) FROM batch_{i}", cancellationToken: TestTimeout.Token())).ShouldBeAssignableTo<DatabaseResultSet>();
            (await SqlT1ScalarTests.ReadAsync(result)).ShouldHaveSingleItem()[0].ShouldBe((long)count);
            // The final reference must be enforced after DDL, as well as accepted during INSERT.
            await SqlConstraintAssertions.ThrowAsync(async () => await session.ExecuteAsync(
                $"INSERT INTO batch_{i} (id, {(operation == "column" ? "anchor" : "pid")}) VALUES ({count + 1}, {count + 2})", cancellationToken: TestTimeout.Token()));
            await session.ExecuteAsync($"DROP TABLE batch_{i}", cancellationToken: TestTimeout.Token());
        }
        return elapsed;
    }

    private static string Insert(string table, int count)
    {
        string number = "a.id + 10*b.id + 100*c.id + 1000*d.id + 10000*e.id + 1";
        string limit = count.ToString(CultureInfo.InvariantCulture);
        return $"INSERT INTO {table} SELECT {number}, CASE WHEN {number} < {limit} THEN {number} + 1 ELSE {limit} END " +
            $"FROM digit a CROSS JOIN digit b CROSS JOIN digit c CROSS JOIN digit d CROSS JOIN digit e WHERE {number} <= {limit}";
    }
}
