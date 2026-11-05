using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectWideMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: repeated columns, stored fan-out and live owners bound payload amplification (#1776)")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ExecuteAsync_SharedWideScale_ShouldReportChargeToHeap(int shape, bool forceBase)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("wide-owner-bound");
        builder.Options.StatementMemoryBudget = 32L << 40;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 4096).Select(k => $"({k})")), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE b(doc TEXT)", cancellationToken: CancellationToken.None);
        if (shape == 1)
        {
            // Stored rows have an 8,092-byte encoding limit; this ASCII value is near it.
            await session.ExecuteAsync("INSERT INTO b VALUES(@p)", new Dictionary<string, object?> { ["p"] = new string('x', 8000) }, CancellationToken.None);
        }
        await session.ExecuteAsync("SELECT 0", cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        string sql = shape switch
        {
            0 => "SELECT " + string.Join(',', Enumerable.Range(0, 256).Select(i => $"@p AS p{i}")) + " FROM t ORDER BY k," + string.Join(',', Enumerable.Range(0, 256).Select(i => $"p{i}")),
            1 => "SELECT b.doc FROM t CROSS JOIN b ORDER BY t.k,b.doc",
            _ => "WITH " + string.Join(',', Enumerable.Range(0, 256).Select(i => $"c{i} AS (SELECT @p AS p)")) + " SELECT @p AS p FROM " + string.Join(" CROSS JOIN ", Enumerable.Range(0, 256).Select(i => $"c{i}")) + " ORDER BY p",
        };
        long expected = shape switch { 0 => 19365976, 1 => forceBase ? 1015216 : 589296, _ => forceBase ? 1073821688 : 1073819680 };
        long legacy = shape switch { 0 => 4398114111488, 1 => forceBase ? 197885952 : 131841752, _ => forceBase ? 1608568800 : 1075884088 };
        long before = GC.GetTotalMemory(true), heap = 0;
        string? payload = shape == 1 ? null : new('x', 1048576);
        int count = 0;
        try
        {
            SqlStatementMetrics.AfterMemoryReservation = (metrics, _) =>
            {
                if (metrics.HashMemoryBytes == expected || metrics.HashMemoryBytes == legacy) { heap = GC.GetTotalMemory(true) - before; }
            };

            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, payload is null ? null : new Dictionary<string, object?> { ["p"] = payload }, CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.FieldCount.ShouldBe(shape == 0 ? 256 : 1); count++; }
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; }

        // Assert
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        _output.WriteLine($"WIDE_SCALE|{shape}|base={forceBase}|charged={metrics.PeakHashMemoryBytes}|heap={heap}|ratio={metrics.PeakHashMemoryBytes / (double)Math.Max(1, heap):F4}");
        metrics.PeakHashMemoryBytes.ShouldBe(expected);
        count.ShouldBe(shape == 2 ? 1 : 4096);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
        GC.KeepAlive(payload);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: detached binary parameters charge each retained clone (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BinaryParameter_ShouldChargeRealClones(bool forceBase)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-identity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)", cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        byte[] payload = new byte[232];
        var retained = new List<object?>();

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT @p AS p FROM t ORDER BY k,p",
            new Dictionary<string, object?> { ["p"] = payload }, CancellationToken.None);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { retained.Add(row.GetValue(0)); }

        // Assert
        retained.Count.ShouldBe(3);
        for (int i = 0; i < retained.Count; i++)
        {
            retained[i].ShouldNotBeSameAs(payload);
            for (int j = 0; j < i; j++) { retained[i].ShouldNotBeSameAs(retained[j]); }
        }
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(1368);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
    }
}
