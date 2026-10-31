using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using static Assimalign.Cohesion.Database.Sql.Tests.SqlPartialIndexTests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlOrderedIndexScalarTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered scalar oracle: IEEE, GUID, binary and temporal codecs equal forced sorts (#1631)")]
    [InlineData("REAL")]
    [InlineData("DOUBLE PRECISION")]
    [InlineData("UUID")]
    [InlineData("BYTEA")]
    [InlineData("TIMESTAMP WITH TIME ZONE")]
    [InlineData("CHAR(8)")]
    [InlineData("TEXT COLLATE binary")]
    [InlineData("TEXT COLLATE case_accent_insensitive")]
    [InlineData("NUMERIC")]
    public async Task ExecuteAsync_ScalarDomains_ShouldEqualForcedSort(string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-scalars", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        object?[] values = type switch
        {
            "REAL" => [null, float.NaN, float.NegativeInfinity, -float.Epsilon, -0f, 0f, float.Epsilon, float.PositiveInfinity, BitConverter.UInt32BitsToSingle(0x7f800001)],
            "DOUBLE PRECISION" => [null, double.NaN, double.NegativeInfinity, -double.Epsilon, -0d, 0d, double.Epsilon, double.PositiveInfinity, BitConverter.UInt64BitsToDouble(0x7ff0000000000001)],
            "UUID" => [null, Guid.Empty, Guid.Parse("ffffffff-0000-0000-0000-000000000000"), Guid.Parse("00000000-ffff-0000-0000-000000000000"), Guid.Parse("00000000-0000-ffff-0000-000000000000"), Guid.Parse("00000000-0000-0000-8000-000000000000")],
            "BYTEA" => [null, Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0, 0 }, new byte[] { 0, 255 }, new byte[] { 1 }, new byte[] { 255 }],
            "CHAR(8)" => [null, "", "a", "a ", "a\0", "ab", "b"],
            "TEXT COLLATE binary" => [null, "", "a", "a\0", "aa", "é", "E", "😀"],
            "TEXT COLLATE case_accent_insensitive" => [null, "é", "E", "e", "É", "a", "b"],
            "NUMERIC" => [null, decimal.MinValue, -1m, -1.000m, 0m, 0.0000000000000000000000000001m, 1.0m, decimal.MaxValue],
            _ => [null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(3)), DateTimeOffset.UnixEpoch.AddTicks(-1), DateTimeOffset.UnixEpoch.AddTicks(1), DateTimeOffset.MaxValue],
        };
        foreach (string table in new[] { "t", "plain" })
        {
            await Execute(session, "CREATE TABLE " + table + "(id INT,k " + type + ")");
            for (int i = 0; i < values.Length; i++)
            {
                await session.ExecuteAsync("INSERT INTO " + table + " VALUES(@id,@k)",
                    new Dictionary<string, object?> { ["id"] = i, ["k"] = values[i] }, CancellationToken.None);
            }
        }
        await Execute(session, "CREATE INDEX ix ON t(k DESC NULLS LAST,id ASC NULLS LAST)");

        // Act / Assert: a unique final key pins zeros, NaNs, padded CHAR and temporal ties.
        foreach (bool reverse in new[] { false, true })
        {
            string order = reverse ? "k ASC NULLS FIRST,id DESC NULLS FIRST" : "k DESC NULLS LAST,id ASC NULLS LAST";
            string tail = " ORDER BY " + order + " LIMIT 5 OFFSET 1";
            var expected = await Rows(session, "SELECT id FROM plain" + tail);
            var actual = await Rows(session, "SELECT id FROM t" + tail);
            actual.ShouldBe(expected);
            session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:" + (reverse ? "backward" : "forward"));
            session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered ranges: physical forward and backward bounds equal forced sorts (#1631)")]
    [InlineData("b>0")]
    [InlineData("b>=0")]
    [InlineData("b<2")]
    [InlineData("b<=2")]
    [InlineData("b BETWEEN 0 AND 2")]
    [InlineData("b>2 AND b<0")]
    public async Task ExecuteAsync_DirectedRange_ShouldRetainOrderedBounds(string predicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-ranges", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "plain" })
        {
            await Execute(session, "CREATE TABLE " + table + "(a INT,b INT)");
            await Execute(session, "INSERT INTO " + table + " VALUES(1,NULL),(1,-1),(1,0),(1,1),(1,2),(1,3),(2,1)");
        }
        await Execute(session, "CREATE INDEX ix ON t(a ASC,b DESC NULLS FIRST)");

        // Act / Assert
        foreach (bool reverse in new[] { false, true })
        {
            string tail = " WHERE a=1 AND " + predicate + " ORDER BY b " + (reverse ? "ASC NULLS LAST" : "DESC NULLS FIRST") + " LIMIT 2 OFFSET 1";
            var expected = await Rows(session, "SELECT b FROM plain" + tail);
            var actual = await Rows(session, "SELECT b FROM t" + tail);
            actual.ShouldBe(expected);
            session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:" + (reverse ? "backward" : "forward"));
            session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ordered DISTINCT: duplicate elimination precedes OFFSET and LIMIT (#1631)")]
    public async Task ExecuteAsync_DistinctOrder_ShouldConsumeAllBeforePagination()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-distinct", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(a INT,k TEXT COLLATE case_insensitive)");
        await Execute(session, "INSERT INTO t VALUES(0,'a'),(0,'A'),(0,'b'),(0,'B'),(0,'c'),(0,NULL)");
        await Execute(session, "CREATE INDEX ix ON t(a,k)");

        // Act
        var rows = await Rows(session, "SELECT DISTINCT k FROM t WHERE a=0 ORDER BY k ASC NULLS LAST LIMIT 2 OFFSET 1");

        // Assert
        rows.Select(row => ((string)row[0]!).ToLowerInvariant()).ShouldBe(["b", "c"]);
        session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:forward");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(6);
        session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ordered counts: volatile LIMIT initializes once after costing (#1631)")]
    public async Task ExecuteAsync_DeferredCount_ShouldExecuteOnlyAtStartup()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("ordered-count");
        builder.Functions.Add(SqlScalarFunction.Create("take_ordered", (long ignored) => { calls++; return 2L; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "INSERT INTO t VALUES(3),(1),(2)");
        await Execute(session, "CREATE INDEX ix ON t(k)");

        // Act
        var rows = await Rows(session, "SELECT k FROM t ORDER BY k ASC NULLS LAST LIMIT take_ordered(0)");

        // Assert
        rows.Select(row => row[0]).ShouldBe([1, 2]);
        calls.ShouldBe(1);
        session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:forward");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Wide order: pooled proof workspace supports more than 64 keys (#1631)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_WideOrder_ShouldMatchEveryComponent(bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ordered-wide", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string[] columns = Enumerable.Range(0, 70).Select(i => "c" + i).ToArray();
        await Execute(session, "CREATE TABLE t(" + string.Join(',', columns.Select(name => name + " INT")) + ")");
        await Execute(session, "CREATE INDEX ix ON t(" + string.Join(',', columns) + ")");
        await Execute(session, "INSERT INTO t VALUES(" + string.Join(',', Enumerable.Repeat("1", columns.Length)) + ")");

        // Act
        var rows = await Rows(session, "SELECT c0 FROM t ORDER BY " + string.Join(',', columns.Select(name => name + (reverse ? " DESC NULLS FIRST" : " ASC NULLS LAST"))) + " LIMIT 1");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1);
        session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:" + (reverse ? "backward" : "forward"));
        session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
    }
}
