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
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: consumed normalized children collect before the next callback (#1776)")]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 64)]
    [InlineData(true, 64)]
    public async Task ExecuteAsync_ConsumedJoinChildren_ShouldCollectWithinLiveCharge(bool forceBase, int count)
    {
        // Arrange: weak references alone survive the producers. The callback runs inside SQL.
        var discarded = new List<WeakReference>();
        var produced = new List<WeakReference>();
        var observations = new List<(int Discarded, long LiveStrings, long Hash, long Plain)>();
        SqlStatementMetrics? current = null;
        var builder = SqlDatabaseEngine.CreateBuilder("consumed-join-children");
        builder.Options.StatementMemoryBudget = 300_000;
        builder.Functions.Add(SqlScalarFunction.Create("watch", (string value) =>
        { discarded.Add(new WeakReference(value)); produced.Add(new WeakReference(value)); return value.Length; }, SqlFunctionVolatility.Immutable));
        builder.Functions.Add(SqlScalarFunction.Create("make", (long k) =>
        { string value = new('x', 2000); produced.Add(new WeakReference(value)); return value; }, SqlFunctionVolatility.Immutable));
        builder.Functions.Add(SqlScalarFunction.Create("inspect", (long k) =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            observations.Add((discarded.Count(reference => reference.IsAlive),
                produced.Count(reference => reference.IsAlive) * 4024L, current!.HashMemoryBytes, current.PlainSelectMemoryBytes));
            return k;
        }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE a(k INT,note TEXT)", "CREATE TABLE b(k INT)", "CREATE TABLE c(k INT)",
            "INSERT INTO b VALUES(2)", "INSERT INTO c VALUES" + string.Join(',', Enumerable.Repeat("(2)", count)) })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("INSERT INTO a VALUES" + string.Join(',', Enumerable.Repeat("(1,@doc)", count)),
            new Dictionary<string, object?> { ["doc"] = new string('x', 2000) }, CancellationToken.None);
        await session.ExecuteAsync("SELECT 0", cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        string query = count == 1
            ? "SELECT inspect(c.k) FROM a RIGHT JOIN b ON watch(a.note)>0 AND a.k>=b.k RIGHT JOIN c ON b.k>=c.k"
            : "SELECT make(c.k),inspect(c.k) FROM a RIGHT JOIN b " +
                "ON watch(a.note)>0 AND a.k>=b.k RIGHT JOIN c ON b.k>=c.k";

        // Act
        int rows = 0;
        try
        {
            SqlStatementMetrics.AfterMemoryReservation = (metrics, _) => current = metrics;
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; }

        // Assert: live string objects must fit the live charge, even below the old false peak.
        rows.ShouldBe(count);
        discarded.Count.ShouldBe(count);
        observations.Count.ShouldBe(count);
        _output.WriteLine($"CONSUMED_CHILD_SUMMARY|base={forceBase}|n={count}|alive={observations.Max(item => item.Discarded)}|strings={observations.Max(item => item.LiveStrings)}|last-live={observations[^1].Plain}|peak={session.LastStatementMetrics!.PeakHashMemoryBytes}");
        foreach (var observed in observations)
        {
            _output.WriteLine($"CONSUMED_CHILD|base={forceBase}|n={count}|discarded={observed.Discarded}|strings={observed.LiveStrings}|hash={observed.Hash}|plain={observed.Plain}");
            observed.Discarded.ShouldBe(0);
            observed.LiveStrings.ShouldBeLessThanOrEqualTo(observed.Plain);
            observed.Hash.ShouldBe(observed.Plain);
        }
        if (count == 1 && !forceBase) { observations[0].Plain.ShouldBe(240); }
        session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(count == 1 ? 4568 : forceBase ? 278016 : 277232);
        observations.Max(item => item.LiveStrings).ShouldBe(count == 1 ? 0 : 257536);
        _output.WriteLine($"CONSUMED_CHILD_PEAK|base={forceBase}|n={count}|{session.LastStatementMetrics!.PeakHashMemoryBytes}");
        Released(session);
    }

}
