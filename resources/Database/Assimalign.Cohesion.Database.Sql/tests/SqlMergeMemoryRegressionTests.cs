using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL upsert concurrency")]
public sealed class SqlMergeMemoryRegressionTests
{
    private readonly ITestOutputHelper _output;

    public SqlMergeMemoryRegressionTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: SET actions charge live projection deltas (#1715)")]
    [InlineData(false, "SET NULL")]
    [InlineData(false, "SET DEFAULT")]
    [InlineData(true, "SET NULL")]
    [InlineData(true, "SET DEFAULT")]
    public async Task ExecuteAsync_LargeSetActionMerge_ShouldStayWithinLinearGrant(bool crossTable, string policy)
    {
        // Arrange / Act: each control uses exactly the same target and source shape.
        long control = await MeasureAsync("CASCADE", crossTable);
        long peak = await MeasureAsync(policy, crossTable);

        // Assert
        _output.WriteLine($"cross_table={crossTable}; policy={policy}; cascade_peak={control}; set_peak={peak}");
        peak.ShouldBeLessThan(control * 3);
    }

    private async Task<long> MeasureAsync(string policy, bool crossTable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = SqlDatabaseEngine.Create("merge-set-memory", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        int rows = crossTable ? 4000 : 1024, deleted = crossTable ? 1000 : 512;
        await session.ExecuteAsync(crossTable ? "CREATE TABLE t(id INT PRIMARY KEY,v INT)" :
            "CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE " + policy + ",v INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, rows).Select(id => crossTable ? $"({id},0)" : $"({id},NULL,0)")), cancellationToken: timeout.Token);
        if (crossTable)
        {
            await session.ExecuteAsync("CREATE TABLE child(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE " + policy + ")", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO child VALUES" + string.Join(',', Enumerable.Range(1, deleted).Select(id => $"({id},{id})")), cancellationToken: timeout.Token);
        }
        try
        {
            var result = await session.ExecuteAsync("MERGE INTO t USING (SELECT id FROM t) s ON t.id=s.id " +
                $"WHEN MATCHED AND t.id<={deleted} THEN DELETE WHEN MATCHED THEN UPDATE SET v=1", cancellationToken: timeout.Token);
            result.AffectedCount.ShouldBe(rows);
        }
        catch
        {
            _output.WriteLine($"cross_table={crossTable}; policy={policy}; refused_peak={session.LastStatementMetrics!.PeakHashMemoryBytes}");
            throw;
        }
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        long peak = metrics.PeakHashMemoryBytes;
        _output.WriteLine($"cross_table={crossTable}; policy={policy}; peak={peak}");
        metrics.HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t", timeout.Token)).ShouldBe([(rows - deleted).ToString()]);
        if (crossTable)
        {
            (await RowsAsync(session, "SELECT COUNT(*) FROM child WHERE pid IS NOT NULL", timeout.Token)).ShouldBe(["0"]);
            (await RowsAsync(session, "SELECT COUNT(*) FROM child", timeout.Token)).ShouldBe([policy == "CASCADE" ? "0" : deleted.ToString()]);
        }
        return peak;
    }

    private static async Task<string[]> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken token)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new System.Collections.Generic.List<string>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(string.Join(',', Enumerable.Range(0, row.FieldCount).Select(row.GetValue))); }
        return rows.ToArray();
    }
}
