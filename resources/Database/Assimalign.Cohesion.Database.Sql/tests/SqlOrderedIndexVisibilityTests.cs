using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Transactions;

using static Assimalign.Cohesion.Database.Sql.Tests.SqlPartialIndexTests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlReadCommittedStressCollection.Name)]
public sealed class SqlOrderedIndexVisibilityTests
{
    private readonly ITestOutputHelper _output;

    public SqlOrderedIndexVisibilityTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered MVCC: forward/backward readers retain statement visibility through commit and rollback (#1631)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ConcurrentKeyChanges_ShouldPreserveStatementSnapshot(bool reverse, bool snapshot)
    {
        // Arrange: the callback pauses after the first heap fetch, outside every tree latch.
        using var release = new ManualResetEventSlim();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int armed = 0, pauses = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("ordered-visibility");
        builder.Functions.Add(SqlScalarFunction.Create("pause_ordered", (long value) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                Interlocked.Increment(ref pauses);
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            }
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(writer, "CREATE TABLE t(id INT PRIMARY KEY,k INT)");
        await Execute(writer, "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 96).Select(i => $"({i},{i})")));
        await Execute(writer, "CREATE INDEX ix ON t(k DESC)");
        string sql = "SELECT id FROM t WHERE pause_ordered(id)>=0 ORDER BY k " + (reverse ? "ASC NULLS LAST" : "DESC NULLS FIRST") + " LIMIT 20";
        var elapsed = Stopwatch.StartNew();
        int rounds = 0, committed = 0, rolledBack = 0;

        // Act: minimum progress plus counters, with time used only for coverage and hang guards.
        do
        {
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            release.Reset();
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var pin = snapshot ? await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None) : null;
            Volatile.Write(ref armed, 1);
            var select = Task.Factory.StartNew(() => Rows(reader, sql), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await Execute(writer, "UPDATE t SET k=k+1000");
                committed++;
                await using (var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None))
                {
                    await Execute(writer, "UPDATE t SET k=-k");
                    await Execute(writer, "DELETE FROM t WHERE id=1 OR id=94");
                    await transaction.RollbackAsync(CancellationToken.None);
                    rolledBack++;
                }
            }
            finally { release.Set(); }

            // Assert: the old snapshot's key order and complete rows survive both mutations.
            var rows = await select.WaitAsync(TimeSpan.FromSeconds(30));
            rows.Select(row => row[0]).ShouldBe(reverse ? Enumerable.Range(0, 20).Cast<object?>()
                : Enumerable.Range(0, 96).Reverse().Take(20).Cast<object?>());
            reader.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix:" + (reverse ? "backward" : "forward"));
            reader.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
            if (pin is not null) { await pin.RollbackAsync(CancellationToken.None); }
            rounds++;
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || rounds < 12);

        rounds.ShouldBeGreaterThanOrEqualTo(12);
        pauses.ShouldBe(rounds);
        committed.ShouldBe(rounds);
        rolledBack.ShouldBe(rounds);
        _output.WriteLine($"MVCC overlap: {rounds} ordered scans, {committed} commits, {rolledBack} rollbacks, {pauses} paused scans.");
    }
}
