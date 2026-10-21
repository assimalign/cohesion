using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlCascadeCatalogTests
{
    private readonly ITestOutputHelper _output;

    public SqlCascadeCatalogTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cascade catalog: incoming discovery is once per table at 1 and 200 tables (#1198)")]
    [InlineData("CASCADE")]
    [InlineData("RESTRICT")]
    public async Task Delete_LargeCatalog_ShouldDiscoverReferencesOnce(string action)
    {
        // Arrange
        double[] minimum = [double.MaxValue, double.MaxValue];
        for (int size = 0; size < 2; size++)
        {
            int tables = size == 0 ? 1 : 200;
            await using var engine = SqlDatabaseEngine.Create("catalog-scaling", new());
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync($"CREATE TABLE chain (id INT PRIMARY KEY, pid INT, FOREIGN KEY(pid) REFERENCES chain(id) ON DELETE {action})");
            await session.ExecuteAsync("CREATE INDEX ix_pid ON chain(pid)");
            for (int table = 1; table < tables; table++)
            {
                await session.ExecuteAsync($"CREATE TABLE unrelated_{table} (id INT, CHECK(id > 0))");
            }

            // Act: fresh rows for each round; setup and verification are not timed.
            for (int round = 0; round < 4; round++)
            {
                await session.ExecuteAsync("INSERT INTO chain VALUES " + string.Join(',', Enumerable.Range(1, 512)
                    .Select(id => $"({id},{(id == 1 ? "NULL" : (id - 1).ToString())})")));
                long start = Stopwatch.GetTimestamp();
                var result = await session.ExecuteAsync(action == "CASCADE" ? "DELETE FROM chain WHERE id = 1" : "DELETE FROM chain",
                    cancellationToken: TestTimeout.Token(30));
                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (round > 0) { minimum[size] = Math.Min(minimum[size], milliseconds); }

                // Assert: the catalog walk depends on distinct tables reached, never row count.
                result.AffectedCount.ShouldBe(action == "CASCADE" ? 1 : 512);
                var metrics = session.LastStatementMetrics.ShouldNotBeNull();
                metrics.IncomingReferenceBuilds.ShouldBe(1);
                metrics.ReferenceCatalogTableVisits.ShouldBe(tables);
                metrics.ReferenceIntentLockBatches.ShouldBe(2);
                metrics.LiveIndexBuilds.ShouldBe(1);
                metrics.ConstraintLookupBuilds.ShouldBe(action == "CASCADE" ? 1 : 2);
                _output.WriteLine($"{action}: catalog={tables}, round={round}, ms={milliseconds:F3}, catalog visits={metrics.ReferenceCatalogTableVisits}");
            }
        }
        _output.WriteLine($"minimum of 3: 1 table={minimum[0]:F3}ms, 200 tables={minimum[1]:F3}ms, ratio={minimum[1] / minimum[0]:F3}");
        minimum[1].ShouldBeLessThan(minimum[0] * 5);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cascade catalog: SET actions prepare adjacent table locks once (#1198)")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task Delete_SetActionFanOut_ShouldPrepareTablesOnce(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("set-lock-batches", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync($"CREATE TABLE c (id INT PRIMARY KEY, pid INT DEFAULT NULL, FOREIGN KEY(pid) REFERENCES p(id) ON DELETE {action})");
        await session.ExecuteAsync("INSERT INTO p VALUES (1)");
        await session.ExecuteAsync("INSERT INTO c VALUES " + string.Join(',', Enumerable.Range(1, 128).Select(id => $"({id},1)")));

        // Act
        await session.ExecuteAsync("DELETE FROM p", cancellationToken: TestTimeout.Token());

        // Assert
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        metrics.IncomingReferenceBuilds.ShouldBe(2);
        metrics.ReferenceCatalogTableVisits.ShouldBe(4);
        metrics.ReferenceIntentLockBatches.ShouldBe(4);
        (await session.ExecuteAsync("DELETE FROM c WHERE pid IS NULL")).AffectedCount.ShouldBe(128);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade catalog: snapshot transactions check live incoming definitions (#1198)")]
    public async Task Delete_ForeignKeyAddedAfterBegin_ShouldCheckLiveDefinition()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("snapshot-catalog", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var setup = await database.CreateSessionAsync();
        await using var session = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await setup.ExecuteAsync("INSERT INTO p VALUES (1)");
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot);
        await setup.ExecuteAsync("CREATE TABLE c (pid INT, CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id) ON DELETE RESTRICT)");
        await setup.ExecuteAsync("INSERT INTO c VALUES (1)");

        // Act
        var failure = await SqlConstraintAssertions.ThrowAsync(() => session.ExecuteAsync("DELETE FROM p").AsTask());
        await transaction.RollbackAsync();
        await setup.ExecuteAsync("ALTER TABLE c DROP CONSTRAINT fk");

        // Assert: the next statement discovers its own graph, even on the same executor.
        failure.ConstraintName.ShouldBe("fk");
        (await session.ExecuteAsync("DELETE FROM p")).AffectedCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade catalog: DDL during a child intent wait refuses stale edges and releases auto-commit locks (#1198)")]
    public async Task Delete_ConcurrentChildDefinitionChange_ShouldRefuseStaleGraphAndReleaseLocks()
    {
        // Arrange: publish DDL under the same X object lock that session DDL takes. This
        // lets the test place publication exactly between discovery and the child's IS grant.
        await using var engine = SqlDatabaseEngine.Create("catalog-race", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var setup = await database.CreateSessionAsync();
        await using var deleting = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await setup.ExecuteAsync("INSERT INTO p VALUES (1)");
        await setup.ExecuteAsync("INSERT INTO c VALUES (1,1)");
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        int waits = 0, refused = 0, cancelled = 0;
        using var listener = new LockWaitListener("db");
        listener.Observed = written =>
        {
            if (written.EventName == "LockWaitStart") { Interlocked.Increment(ref waits); }
        };
        var timer = Stopwatch.StartNew();

        // Act: alternate definition failure and cancellation while queued, with an actual
        // wait required each round. Repeat for a minimum time and until both paths moved.
        do
        {
            int previous = Volatile.Read(ref waits);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var cancellation = new CancellationTokenSource();
            var holder = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
            try
            {
                await database.Coordinator.LockManager.AcquireAsync(holder.Sequence,
                    LockResource.Object(child.ObjectId), LockMode.Exclusive, timeout.Token);
                var pending = deleting.ExecuteAsync("DELETE FROM p", cancellationToken: cancellation.Token).AsTask();
                while (Volatile.Read(ref waits) == previous) { await Task.Delay(1, timeout.Token); }
                if ((refused + cancelled) % 2 == 0)
                {
                    await database.Catalog.DropConstraintAsync("dbo", "c", "fk", timeout.Token);
                    await database.Coordinator.RollbackAsync(holder, timeout.Token);
                    var failure = await Should.ThrowAsync<DatabaseException>(async () => await pending);
                    failure.Message.ShouldContain("changed while the statement was waiting", Case.Sensitive);
                    refused++;
                    await setup.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE", cancellationToken: timeout.Token);
                }
                else
                {
                    cancellation.Cancel();
                    await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
                    cancelled++;
                    await database.Coordinator.RollbackAsync(holder, timeout.Token);
                }
                database.Catalog.TryGetTable("dbo", "c", out child).ShouldBeTrue();
                // An X grant after each failed auto-commit proves its IX/IS grants released.
                await setup.ExecuteAsync("ALTER TABLE p ALTER COLUMN id SET DEFAULT 1", cancellationToken: timeout.Token);
                await setup.ExecuteAsync("ALTER TABLE p ALTER COLUMN id DROP DEFAULT", cancellationToken: timeout.Token);
            }
            finally
            {
                cancellation.Cancel();
                if (holder.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(holder, CancellationToken.None); }
            }
        } while ((timer.Elapsed < TimeSpan.FromSeconds(1) || refused < 3 || cancelled < 3) && timer.Elapsed < TimeSpan.FromSeconds(30));

        // Assert
        refused.ShouldBeGreaterThanOrEqualTo(3);
        cancelled.ShouldBeGreaterThanOrEqualTo(3);
        Volatile.Read(ref waits).ShouldBeGreaterThanOrEqualTo(refused + cancelled);
        _output.WriteLine($"waits={waits}, stale refusals={refused}, cancellations={cancelled}");
        (await setup.ExecuteAsync("DELETE FROM p")).AffectedCount.ShouldBe(1);
    }
}
