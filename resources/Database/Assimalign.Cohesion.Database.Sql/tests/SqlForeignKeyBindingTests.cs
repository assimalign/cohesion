using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlForeignKeyBindingTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - FK binding: both endpoint versions and reordered composite indexes bind once (#1366)")]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task Delete_DroppedUnrelatedColumns_ShouldUseBothNewEndpointOrdinals(string action)
    {
        // Arrange: both keys shift logical ordinals independently. Index order differs
        // from FK order, and string equality must keep the parent's effective collation.
        await using var engine = SqlDatabaseEngine.Create("fk-binding", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (unused INT, a TEXT COLLATE case_insensitive, b INT, CONSTRAINT uq UNIQUE(a,b))");
        await session.ExecuteAsync($"CREATE TABLE c (unused INT, id INT PRIMARY KEY, pb INT, pa TEXT COLLATE case_insensitive, CONSTRAINT fk FOREIGN KEY(pa,pb) REFERENCES p(a,b) ON DELETE {action})");
        await session.ExecuteAsync("CREATE INDEX ix_child ON c(pb,pa)");
        await session.ExecuteAsync("INSERT INTO p VALUES (0,'key',7)");
        await session.ExecuteAsync("INSERT INTO c VALUES (0,1,7,'KEY')");
        await session.ExecuteAsync("ALTER TABLE p DROP COLUMN unused");
        await session.ExecuteAsync("UPDATE c SET pa = 'Key'");
        await session.ExecuteAsync("ALTER TABLE c DROP COLUMN unused");
        await session.ExecuteAsync("INSERT INTO c VALUES (1,7,'kEy') ON CONFLICT(id) DO UPDATE SET pa = EXCLUDED.pa");
        database.Catalog.TryGetTable("dbo", "p", out var parent).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        var constraint = child.Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.Reference);
        var parentBound = database.Definitions.Get(parent);
        var childBound = database.Definitions.Get(child);
        parentBound.GetKey(constraint.ReferencedColumns!).Ordinals.ShouldBe([0, 1]);
        childBound.GetKey(constraint.Columns).Ordinals.ShouldBe([2, 1]);
        int parentBindings = parentBound.KeyBindCount;
        int childBindings = childBound.KeyBindCount;

        // Act: repeated row checks and seeks reuse the exact endpoint projections.
        for (int round = 0; round < 3; round++)
        {
            await using var transaction = await session.BeginTransactionAsync();
            await session.ExecuteAsync("DELETE FROM p");
            var metrics = session.LastStatementMetrics.ShouldNotBeNull();
            metrics.ConstraintLookupBuilds.ShouldBe(action == "CASCADE" ? 1 : 2);
            metrics.LiveIndexBuilds.ShouldBe(2);
            (await session.ExecuteAsync(action == "CASCADE" ? "DELETE FROM c" : "DELETE FROM c WHERE pa IS NULL AND pb IS NULL"))
                .AffectedCount.ShouldBe(action == "CASCADE" ? 0 : 1);
            await transaction.RollbackAsync();
        }

        // Assert
        parentBound.KeyBindCount.ShouldBe(parentBindings);
        childBound.KeyBindCount.ShouldBe(childBindings);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - FK binding: concurrent parent key binding publishes one projection (#1366)")]
    public async Task GetKey_ConcurrentFirstUse_ShouldBindOnce()
    {
        // Arrange: parent projections are lazy because their constraints live on children.
        await using var engine = SqlDatabaseEngine.Create("fk-key-race", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (a INT, b INT, UNIQUE(a,b))");
        database.Catalog.TryGetTable("dbo", "p", out var table).ShouldBeTrue();
        var bound = database.Definitions.Get(table);
        string[] columns = ["b", "a"];
        int before = bound.KeyBindCount;
        int ready = 0, visits = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = Stopwatch.StartNew();

        // Act: release four first users together, then run for a minimum time and work count.
        var workers = Enumerable.Range(0, 4).Select(_ => DedicatedTestTask.Run(async () =>
        {
            Interlocked.Increment(ref ready);
            await start.Task.WaitAsync(deadline.Token);
            var first = bound.GetKey(columns);
            do
            {
                deadline.Token.ThrowIfCancellationRequested();
                bound.GetKey(columns).ShouldBeSameAs(first);
                Interlocked.Increment(ref visits);
            } while (timer.Elapsed < TimeSpan.FromSeconds(1) || Volatile.Read(ref visits) < 1000);
            return first;
        }, deadline.Token)).ToArray();
        while (Volatile.Read(ref ready) < workers.Length) { await Task.Delay(1, deadline.Token); }
        start.SetResult();
        var keys = await Task.WhenAll(workers);

        // Assert
        visits.ShouldBeGreaterThanOrEqualTo(1000);
        foreach (var key in keys) { key.ShouldBeSameAs(keys[0]); }
        keys[0].Ordinals.ShouldBe([1, 0]);
        bound.KeyBindCount.ShouldBe(before + 1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - FK binding: next statement observes a replaced lookup index (#1366)")]
    public async Task Delete_IndexReplacedBetweenStatements_ShouldResolveCurrentTree()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("fk-index-version", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX ix ON c(pid)");
        await session.ExecuteAsync("INSERT INTO p VALUES (1)");
        await session.ExecuteAsync("INSERT INTO c VALUES (1,1)");
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await session.ExecuteAsync("DELETE FROM p");
            await transaction.RollbackAsync();
        }

        // Act
        await session.ExecuteAsync("DROP INDEX ix ON c");
        await session.ExecuteAsync("CREATE INDEX ix ON c(pid,id)");
        await session.ExecuteAsync("DELETE FROM p");

        // Assert
        session.LastStatementMetrics.ShouldNotBeNull().ConstraintLookupBuilds.ShouldBe(1);
        session.LastStatementMetrics!.LiveIndexBuilds.ShouldBe(2);
        (await session.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(0);
    }
}
