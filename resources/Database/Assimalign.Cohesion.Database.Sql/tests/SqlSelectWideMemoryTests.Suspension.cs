using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectWideMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: a wide group owner survives a recorded lock suspension and cancellation (#1776)")]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExecuteAsync_WideGroupLockWait_ShouldPreserveOwnerAndUnwind(bool forceBase, bool cancel)
    {
        // Arrange: the aggregate's correlated subquery executes after the group key admission.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = SqlDatabaseEngine.Create("wide-group-suspension", new());
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var setup = await database.CreateSessionAsync(guard.Token);
        foreach (string sql in new[] { "CREATE TABLE t(k INT)", "INSERT INTO t VALUES(1),(2)",
            "CREATE TABLE u(k INT)", "INSERT INTO u VALUES(1),(2)", "CREATE TABLE v(k INT)", "INSERT INTO v VALUES(1)" })
        { await setup.ExecuteAsync(sql, cancellationToken: guard.Token); }
        var parameters = new Dictionary<string, object?> { ["p"] = new string('x', 200) };
        const string query = "SELECT @p,k,SUM((SELECT MAX(u.k) FROM u CROSS JOIN v WHERE u.k=t.k)) FROM t GROUP BY @p,k ORDER BY k";
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        var reference = await ReadRows(setup, guard.Token);
        long peak = setup.LastStatementMetrics!.PeakHashMemoryBytes;
        Released(setup);
        await using var holderSession = await database.CreateSessionAsync(guard.Token);
        await using var holder = await holderSession.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        database.Catalog.TryGetTable("dbo", "u", out var table).ShouldBeTrue();
        database.Coordinator.LockManager.TryAcquire(holder.Context.Sequence, LockResource.Object(table!.ObjectId), LockMode.Exclusive).ShouldBeTrue();
        await using var contender = await database.CreateSessionAsync(guard.Token);
        await using var transaction = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        using var waits = new LockWaitListener(database.Name);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        SqlStatementMetrics? current = null;
        Task<List<string>>? pending = null;

        // Act
        try
        {
            SqlStatementMetrics.AfterMemoryReservation = (metrics, _) => current = metrics;
            pending = ReadRows(contender, canceled.Token);
            await waits.WaitForWaitAsync(transaction.Context.Sequence, guard.Token);
            pending.IsCompleted.ShouldBeFalse();
            current.ShouldNotBeNull().PlainSelectMemoryBytes.ShouldBeGreaterThanOrEqualTo(624);
            _output.WriteLine($"GROUP_SUSPENDED|base={forceBase}|cancel={cancel}|hash={current.HashMemoryBytes}|plain={current.PlainSelectMemoryBytes}");
            if (cancel)
            {
                canceled.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
            }
            else
            {
                await holder.RollbackAsync(guard.Token);
                (await pending).ShouldBe(reference);
                contender.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(peak);
            }
            Released(contender);
            _output.WriteLine($"GROUP_RESUMED|base={forceBase}|cancel={cancel}|reference={peak}|peak={contender.LastStatementMetrics!.PeakHashMemoryBytes}");
        }
        finally
        {
            SqlStatementMetrics.AfterMemoryReservation = null;
            canceled.Cancel();
            if (holder.State == TransactionState.Active) { await holder.RollbackAsync(CancellationToken.None); }
            if (pending is not null)
            { try { await pending; } catch (OperationCanceledException) { } }
        }

        // Assert
        await transaction.RollbackAsync(guard.Token);

        async Task<List<string>> ReadRows(SqlDatabaseSession session, CancellationToken token)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, parameters, token);
            var rows = new List<string>();
            await foreach (var row in result.GetRowsAsync(token)) { rows.Add($"{row.GetValue(0)}|{row.GetValue(1)}|{row.GetValue(2)}"); }
            return rows;
        }
    }
}
