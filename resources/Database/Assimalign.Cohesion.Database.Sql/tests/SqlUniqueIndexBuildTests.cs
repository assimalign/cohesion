using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlUniqueIndexBuildTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unique builds: sorted live duplicates across cursor batches fail before publication (#1444)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_DuplicatesAcrossBatches_ShouldCleanUpAndAllowRetry(bool deferred, bool constraint)
    {
        // Arrange: 64 preceding keys put the equal pair across the cursor's first
        // full batch boundary. Heap order separates them; collation equates them.
        await using var engine = SqlDatabaseEngine.Create("unique-build", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, k TEXT COLLATE case_insensitive)", cancellationToken: TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(0, 192)
            .Select(id => $"({id},'{(id == 64 ? "key064" : id == 191 ? "KEY064" : $"key{id:D3}")}')")), cancellationToken: TestTimeout.Token());
        if (deferred) { session.ProvisioningProgress = (_, _) => { }; }
        string sql = constraint ? "ALTER TABLE t ADD CONSTRAINT ix UNIQUE(k)" : "CREATE UNIQUE INDEX ix ON t(k)";

        // Act
        var failure = await SqlConstraintAssertions.ThrowAsync(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

        // Assert: no catalog description or in-memory registration survives failure.
        failure.ConstraintName.ShouldBe("ix");
        failure.Table.ShouldBe("dbo.t");
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE037");
        failure.OffendingValue.ShouldBeNull();
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        database.Catalog.GetIndexes(table.ObjectId).ShouldBeEmpty();
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        await session.ExecuteAsync("DELETE FROM t WHERE id = 191", cancellationToken: TestTimeout.Token());
        (await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token())).Status.ShouldBe(DatabaseResultStatus.Success);
        database.IndexManager.ExportRegistrations().ShouldHaveSingleItem();
        await SqlConstraintAssertions.ThrowAsync(async () => await session.ExecuteAsync("INSERT INTO t VALUES (999,'KEY064')", cancellationToken: TestTimeout.Token()));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unique builds: tombstoned history is retained for old snapshots without false duplicates (#1444)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_RetainedHistory_ShouldValidateLatestAndServeOldSnapshot(bool deferred, bool constraint)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("unique-history", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: TestTimeout.Token());
        await writer.ExecuteAsync("INSERT INTO t VALUES (1,7),(2,8)", cancellationToken: TestTimeout.Token());
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var pinned = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await writer.ExecuteAsync("UPDATE t SET v = v + 1", cancellationToken: TestTimeout.Token());
        if (deferred) { writer.ProvisioningProgress = (_, _) => { }; }

        // Act
        await writer.ExecuteAsync(constraint ? "ALTER TABLE t ADD CONSTRAINT ix UNIQUE(v)" : "CREATE UNIQUE INDEX ix ON t(v)", cancellationToken: TestTimeout.Token());

        // Assert: old versions of keys 7/8 and live versions 8/9 coexist in the tree.
        await using var result = (await reader.ExecuteAsync("SELECT id FROM t WHERE v = 8", cancellationToken: TestTimeout.Token())).ShouldBeAssignableTo<DatabaseResultSet>();
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldHaveSingleItem()[0].ShouldBe(2);
        await SqlConstraintAssertions.ThrowAsync(async () => await writer.ExecuteAsync("INSERT INTO t VALUES (3,8)", cancellationToken: TestTimeout.Token()));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unique builds: latest duplicates cannot hide behind the DDL transaction snapshot (#1444)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OlderDdlSnapshot_ShouldRejectNewLiveDuplicates(bool deferred)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("unique-latest", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await ddl.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: TestTimeout.Token());
        // Drive the executor's DDL context directly: sessions refuse explicit DDL.
        // An auto-commit DDL can likewise capture its snapshot before a writer commits
        // while it waits for table X, so validation must ignore that older snapshot.
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        try
        {
            await writer.ExecuteAsync("INSERT INTO t VALUES (7),(7)", cancellationToken: TestTimeout.Token());
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var statement = new SqlStatementContext(transaction, database.Coordinator,
                provisioningProgress: deferred ? (_, _) => { } : null);

            // Act
            var failure = await SqlConstraintAssertions.ThrowAsync(async () => await executor.ExecuteAsync(
                SqlQueryRequest.FromSql("CREATE UNIQUE INDEX ix ON t(id)"), statement, TestTimeout.Token()));

            // Assert
            failure.ConstraintName.ShouldBe("ix");
            if (!deferred) { failure.OffendingValue.ShouldBe(7); }
            database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unique builds: NULL components keep their existing duplicate identity (#1444)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_NullCompositeDuplicates_ShouldRefusePublication(bool deferred, bool constraint)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("unique-null", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT, b INT)", cancellationToken: TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t VALUES (1,NULL),(2,NULL),(1,NULL)", cancellationToken: TestTimeout.Token());
        if (deferred) { session.ProvisioningProgress = (_, _) => { }; }

        // Act
        var failure = await SqlConstraintAssertions.ThrowAsync(async () => await session.ExecuteAsync(constraint
            ? "ALTER TABLE t ADD CONSTRAINT ix UNIQUE(a,b)" : "CREATE UNIQUE INDEX ix ON t(a,b)", cancellationToken: TestTimeout.Token()));

        // Assert
        failure.OffendingValue.ShouldBeNull();
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unique builds: cancellation after streaming releases the unpublished tree and DDL locks (#1444)")]
    [InlineData("CREATE UNIQUE INDEX ix ON t(id)")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ix UNIQUE(id)")]
    [InlineData("ALTER TABLE t ADD COLUMN v INT NOT NULL DEFAULT 1")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck CHECK(id > 0)")]
    public async Task ExecuteAsync_CanceledStreaming_ShouldAllowAnotherSessionToRetry(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("ddl-cancel", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t VALUES (1),(2),(3)", cancellationToken: TestTimeout.Token());
        using var canceled = new CancellationTokenSource();
        int progressed = 0;
        session.ProvisioningProgress = (processed, _) =>
        {
            if (processed > 0) { progressed++; canceled.Cancel(); }
        };

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(sql, cancellationToken: canceled.Token));

        // Assert
        progressed.ShouldBeGreaterThan(0);
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        await using var retry = await database.CreateSessionAsync(CancellationToken.None);
        (await retry.ExecuteAsync(sql, cancellationToken: TestTimeout.Token())).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Unique builds: waiting writers see published enforcement after the sorted validation pass (#1444)")]
    public async Task ExecuteAsync_WriterWaitsDuringBuild_ShouldEnforcePublishedIndex()
    {
        // Arrange: run alone until a minimum interval and observed lock overlaps,
        // bounded by a ceiling. No timer decides whether the race was exercised.
        await using var engine = SqlDatabaseEngine.Create("unique-build-race", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int blocked = 0;
        int refused = 0;
        var elapsed = Stopwatch.StartNew();

        // Act
        do
        {
            ceiling.Token.ThrowIfCancellationRequested();
            await ddl.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: ceiling.Token);
            await ddl.ExecuteAsync("INSERT INTO t VALUES (1),(2)", cancellationToken: ceiling.Token);
            Task<DatabaseResult>? pending = null;
            ddl.ProvisioningProgress = (processed, _) =>
            {
                if (processed == 0 || pending is not null) { return; }
                pending = writer.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: ceiling.Token).AsTask();
                pending.IsCompleted.ShouldBeFalse("the writer must actually wait behind the build's table X lock");
                blocked++;
            };
            try { await ddl.ExecuteAsync("CREATE UNIQUE INDEX ix ON t(id)", cancellationToken: ceiling.Token); }
            finally { ddl.ProvisioningProgress = null; }
            await SqlConstraintAssertions.ThrowAsync(async () => await pending.ShouldNotBeNull());
            refused++;
            await ddl.ExecuteAsync("DROP TABLE t", cancellationToken: ceiling.Token);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || blocked < 10);

        // Assert
        blocked.ShouldBeGreaterThanOrEqualTo(10);
        refused.ShouldBe(blocked);
    }
}
