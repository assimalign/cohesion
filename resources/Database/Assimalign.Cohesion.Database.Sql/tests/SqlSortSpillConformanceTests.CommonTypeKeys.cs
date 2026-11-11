using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    public static IEnumerable<object[]> CommonTypeColumnKeys()
    {
        // Two valued cross-type pairs and NULL on the left, right and both sides.
        foreach (var key in new (string Left, string Right, string A, string B)[]
        {
            ("INT", "NUMERIC", "1", "1.0"), ("REAL", "NUMERIC", "0.5", "0.5"),
            ("INT", "NUMERIC", "NULL", "1.0"), ("INT", "NUMERIC", "1", "NULL"),
            ("INT", "NUMERIC", "NULL", "NULL"),
        })
        {
            foreach (string kind in new[] { "INNER", "LEFT", "RIGHT", "FULL" })
            {
                foreach (bool normalized in new[] { false, true })
                { yield return [key.Left, key.Right, key.A, key.B, kind, normalized]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Common-type column join keys match fitting and spilled rows, CLR types and metadata (#1788, #1789)")]
    [MemberData(nameof(CommonTypeColumnKeys))]
    public async Task ExecuteAsync_CommonTypeColumnKeys_ShouldMatchFittingAndSpilled(
        string leftType, string rightType, string leftKey, string rightKey, string kind, bool normalized)
    {
        // Arrange: both runs use Q=67,108,864 B; G=64 MiB and 256 KiB respectively.
        RowValues[] expectedRows = [];
        DatabaseResultSet? metadata = null;
        foreach (long grant in new[] { 67108864L, 262144L })
        {
            await using var engine = await Create("column-keys-" + grant, grant);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await session.ExecuteAsync($"CREATE TABLE a(id INT,k {leftType},payload TEXT)");
            await session.ExecuteAsync($"CREATE TABLE b(id INT,n {rightType},payload TEXT)");
            for (int first = 0; first < 600; first += 100)
            {
                string Values(bool left) => string.Join(',', Enumerable.Range(first, 100).Select(i =>
                {
                    string key = i == 0 ? left ? leftKey : rightKey : (i + 10).ToString(CultureInfo.InvariantCulture);
                    return $"({i},{key},'{new string('x', 512)}')";
                }));
                await session.ExecuteAsync("INSERT INTO a VALUES" + Values(true));
                await session.ExecuteAsync("INSERT INTO b VALUES" + Values(false));
            }
            string source = $"a {kind} JOIN b ON a.k=b.n";
            string sql = $"SELECT a.id,b.id FROM {(normalized ? "(" + source + ")" : source)} ORDER BY a.id NULLS FIRST,b.id NULLS FIRST";
            var plan = new SqlPlanner(session.Database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
            (((SqlJoinPlan)plan).Tree is not null).ShouldBe(normalized || kind is "RIGHT" or "FULL");
            int handoffs = 0;
            SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "join-handoff") { handoffs++; } };
            try
            {
                // Act
                await using var actual = (DatabaseResultSet)await session.ExecuteAsync(sql);
                var rows = await Rows(actual);
                // Assert: RowValues also checks exact CLR types and numeric representations.
                if (grant == 67108864) { expectedRows = rows; metadata = actual; handoffs.ShouldBe(0); }
                else
                {
                    handoffs.ShouldBeGreaterThan(0);
                    rows.ShouldBe(expectedRows); AssertColumnMetadata(actual, metadata!);
                }
                _output.WriteLine($"COMMON_COLUMN_KEY|left={leftType}:{leftKey}|right={rightType}:{rightKey}|kind={kind}|normalized={normalized}|grant={grant}|quota=67108864|handoffs={handoffs}|rows={rows.Length}");
                session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(grant);
            }
            finally { SqlPlanExecutor.ObserveHashSpill = null; }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting computed common-type join keys match hand-derived rows (#1788)")]
    [InlineData("coalesce")]
    [InlineData("char-text")]
    [InlineData("char-char")]
    [InlineData("real-numeric")]
    public async Task ExecuteAsync_ComputedCommonTypeKeys_ShouldMatchDerivedRows(string shape)
    {
        // Arrange: G=67,108,864 B, Q=67,108,864 B. Computed-key spilling is a follow-up.
        await using var engine = await Create("computed-key-fit", 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        string left = shape.StartsWith("char", StringComparison.Ordinal) ? "CHAR(3)" : shape == "real-numeric" ? "REAL" : "INT";
        string right = shape == "char-text" ? "TEXT" : shape == "char-char" ? "CHAR(5)" : "NUMERIC";
        await session.ExecuteAsync($"CREATE TABLE a(id INT,k {left})");
        await session.ExecuteAsync($"CREATE TABLE b(id INT,n {right})");
        bool character = shape.StartsWith("char", StringComparison.Ordinal);
        await session.ExecuteAsync(character ? "INSERT INTO a VALUES(1,'a')" : shape == "real-numeric"
            ? "INSERT INTO a VALUES(1,0.5),(2,NULL)" : "INSERT INTO a VALUES(1,NULL),(2,1),(3,2)");
        await session.ExecuteAsync(character ? "INSERT INTO b VALUES(10,'a'),(11,'a ')" : shape == "real-numeric"
            ? "INSERT INTO b VALUES(10,0.5),(11,0)" : "INSERT INTO b VALUES(10,0),(11,1),(12,3)");
        string key = character ? "COALESCE(a.k,CAST('b' AS CHAR(3)))" : shape == "real-numeric" ? "COALESCE(a.k,CAST(0 AS REAL))" : "COALESCE(a.k,0)";
        int handoffs = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "join-handoff") { handoffs++; } };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT a.id,b.id FROM a JOIN b ON {key}=b.n ORDER BY a.id,b.id");
            var rows = await Rows(result);
            // Assert: TEXT keeps its trailing space; two CHAR values ignore their padding.
            RowValues[] expected = shape switch
            {
                "char-text" => [new([1, 10])],
                "char-char" => [new([1, 10]), new([1, 11])],
                _ => [new([1, 10]), new([2, 11])],
            };
            rows.ShouldBe(expected); handoffs.ShouldBe(0);
            result.Columns.Select(c => c.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int32]);
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - An unresolved polymorphic join key refuses CS02080 before reading input (#1788)")]
    public async Task ExecuteAsync_UnresolvedPolymorphicJoinKey_ShouldRefuseBeforeRows()
    {
        // Arrange
        var calls = new List<long>();
        var builder = SqlDatabaseEngine.CreateBuilder("unresolved-join-key");
        builder.Options.StatementMemoryBudget = 67108864;
        builder.Functions.Add(new HashUnprovedResultFunction(true, text: true));
        builder.Functions.Add(SqlScalarFunction.Create("visit", (long id) => { calls.Add(id); return id; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE a(id INT)"); await session.ExecuteAsync("INSERT INTO a VALUES(500)");
        await session.ExecuteAsync("CREATE TABLE b(n NUMERIC)"); await session.ExecuteAsync("INSERT INTO b VALUES(1)");
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT a.id FROM (SELECT visit(id) id FROM a) a JOIN b ON COALESCE(unproved_result(a.id,NULL),0)=b.n"));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02080");
        calls.ShouldBeEmpty();
        (session.LastStatementMetrics?.RecordsExamined ?? 0).ShouldBe(0);
    }
}
