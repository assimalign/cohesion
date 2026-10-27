using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCreateTableAsTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: older snapshots see an empty destination and later snapshots see every row")]
    public async Task ExecuteAsync_PreBuilderCommitSnapshot_ShouldSeeEmptyDestination()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-old-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var copying = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(writer, timeout.Token);
        await using var before = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
        (await RowsAsync(reader, "SELECT * FROM src", timeout.Token)).Count.ShouldBe(65);
        await ExecAsync(writer, "INSERT INTO src VALUES(66)", timeout.Token);

        // Act
        await ExecAsync(copying, "CREATE TABLE copy AS SELECT * FROM src", timeout.Token);

        // Assert
        (await RowsAsync(reader, "SELECT * FROM copy", timeout.Token)).ShouldBeEmpty();
        (await RowsAsync(reader, "SELECT * FROM src", timeout.Token)).Count.ShouldBe(65);
        await before.CommitAsync(timeout.Token);
        await using var after = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
        (await RowsAsync(reader, "SELECT id FROM copy ORDER BY id", timeout.Token)).Select(r => (int)r[0]!).ShouldBe(Enumerable.Range(1, 66));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: retained source output and encoded batches share and release the statement budget")]
    [InlineData(64)]
    [InlineData(150000)]
    public async Task ExecuteAsync_RetainedMemoryExceedsBudget_ShouldRefuseAndRelease(long budget)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-memory", new() { StatementMemoryBudget = budget });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE src (id INT, value TEXT)", timeout.Token);
        await ExecAsync(session, "INSERT INTO src VALUES " + string.Join(',', Enumerable.Range(1, 65).Select(id => $"({id},'{new string('x', 1024)}')")), timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "CREATE TABLE copy AS SELECT * FROM src ORDER BY id", timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        database.Catalog.TryGetTable("dbo", "copy", out _).ShouldBeFalse();
        await ReclaimAsync(database, timeout.Token);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        await ExecAsync(session, "CREATE TABLE copy AS SELECT * FROM src WITH NO DATA", timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: publication collisions report duplicate names or IF NOT EXISTS no-op")]
    [InlineData("CREATE TABLE copy (n INT)", false)]
    [InlineData("CREATE TABLE copy AS SELECT 99 AS n", false)]
    [InlineData("ALTER TABLE other RENAME TO copy", false)]
    [InlineData("CREATE TABLE copy (n INT)", true)]
    [InlineData("CREATE TABLE copy AS SELECT 99 AS n", true)]
    [InlineData("ALTER TABLE other RENAME TO copy", true)]
    public async Task ExecuteAsync_NamePublishedDuringBuild_ShouldHandleCollision(string competing, bool ifNotExists)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-name-race", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var winner = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session, timeout.Token);
        await ExecAsync(winner, "CREATE TABLE other(n INT)", timeout.Token);
        ulong owner = 0;
        int raced = 0;
        SqlPlanExecutor.TableAsStage = (stage, id) =>
        {
            if (stage != "Prepared" || raced != 0) { return; }
            raced++;
            owner = id;
            ExecAsync(winner, competing, timeout.Token).GetAwaiter().GetResult();
        };
        try
        {
            // Act
            string sql = "CREATE TABLE " + (ifNotExists ? "IF NOT EXISTS " : "") + "copy AS SELECT * FROM src";
            if (ifNotExists) { (await session.ExecuteAsync(sql, cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0); }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, sql, timeout.Token));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03049");
            }
        }
        finally { SqlPlanExecutor.TableAsStage = null; }

        // Assert
        raced.ShouldBe(1);
        database.Catalog.Tables.Single(t => t.Name == "copy").ObjectId.ShouldNotBe(owner);
        await ReclaimAsync(database, timeout.Token);
        database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: completion counts copied rows and zero for empty, no-data and skipped builds")]
    public async Task ExecuteAsync_Completion_ShouldCountWrittenRows()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-count", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session, timeout.Token);

        // Act / Assert
        (await session.ExecuteAsync("CREATE TABLE copy AS SELECT * FROM src", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(65);
        (await session.ExecuteAsync("CREATE TABLE empty AS SELECT * FROM src WHERE id<0", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("CREATE TABLE no_data AS SELECT * FROM src WITH NO DATA", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("CREATE TABLE IF NOT EXISTS copy AS SELECT missing FROM absent", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: a snapshot begun while rows are being built sees no destination rows")]
    [InlineData("Copied")]
    [InlineData("Prepared")]
    public async Task ExecuteAsync_SnapshotDuringBuild_ShouldSeeEmptyDestination(string point)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-active-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(writer, timeout.Token);
        SqlDatabaseTransaction? snapshot = null;
        SqlPlanExecutor.TableAsStage = (stage, _) =>
        {
            if (stage == point && snapshot is null)
            { snapshot = reader.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token).AsTask().GetAwaiter().GetResult(); }
        };
        try
        {
            // Act
            await ExecAsync(writer, "CREATE TABLE copy AS SELECT * FROM src", timeout.Token);

            // Assert
            snapshot.ShouldNotBeNull();
            (await RowsAsync(reader, "SELECT * FROM copy", timeout.Token)).ShouldBeEmpty();
            await snapshot.CommitAsync(timeout.Token);
            (await RowsAsync(reader, "SELECT * FROM copy", timeout.Token)).Count.ShouldBe(65);
        }
        finally
        {
            SqlPlanExecutor.TableAsStage = null;
            if (snapshot is not null) { await snapshot.DisposeAsync(); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: admission in the writer-commit/publication gap proceeds and later binding sees all rows")]
    [InlineData("begin")]
    [InlineData("auto")]
    [InlineData("read-committed")]
    public async Task ExecuteAsync_SnapshotAtPublicationGap_ShouldProceedAndSeeCompleteRows(string mode)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-publication-gap", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(writer, timeout.Token);
        await using var existing = mode == "read-committed" ? await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token) : null;
        Task<SqlDatabaseTransaction>? beginning = null;
        Task<DatabaseResult>? reading = null;
        int observed = 0;
        SqlPlanExecutor.TableAsStage = (stage, owner) =>
        {
            if (stage != "BuilderCommitted") { return; }
            observed++;
            database.Catalog.TryGetTable("dbo", "copy", out _).ShouldBeFalse();
            if (mode == "begin")
            {
                beginning = reader.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token).AsTask();
                beginning.IsCompleted.ShouldBeTrue();
            }
            else
            {
                // Finish an unrelated query while CTAS remains unpublished; the live
                // catalog can bind the new table only after publication.
                reading = reader.ExecuteAsync("SELECT * FROM src", cancellationToken: timeout.Token).AsTask();
                reading.IsCompleted.ShouldBeTrue();
            }
        };
        try
        {
            // Act
            await ExecAsync(writer, "CREATE TABLE copy AS SELECT * FROM src", timeout.Token);

            // Assert
            observed.ShouldBe(1);
            (await RowsAsync(reader, "SELECT id FROM copy ORDER BY id", timeout.Token))
                .Select(r => (int)r[0]!).ShouldBe(Enumerable.Range(1, 65));
        }
        finally
        {
            SqlPlanExecutor.TableAsStage = null;
            if (reading is not null) { await using var result = (DatabaseResultSet)await reading; }
            if (beginning is not null) { await using var snapshot = await beginning; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: BEGIN queued at TRUNCATE admission does not block other statements")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginTransactionAsync_QueuedBehindTruncate_ShouldAllowStatementAdmission(bool explicitTransaction)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("ctas-begin-admission", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var beginning = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(reader, timeout.Token);
        await using var existing = explicitTransaction ? await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token) : null;
        await database.Catalog.TruncateSnapshotGate.WaitAsync(timeout.Token);
        var queued = beginning.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token).AsTask();
        Task<DatabaseResult>? reading = null;
        try
        {
            // Act
            queued.IsCompleted.ShouldBeFalse();
            reading = reader.ExecuteAsync("SELECT * FROM src", cancellationToken: timeout.Token).AsTask();

            // Assert
            reading.IsCompleted.ShouldBeTrue();
            var result = (DatabaseResultSet)await reading;
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(timeout.Token)) { rows++; }
            rows.ShouldBe(65);
        }
        finally
        {
            database.Catalog.TruncateSnapshotGate.Release();
            await using var snapshot = await queued;
            if (reading is not null) { await using var result = (DatabaseResultSet)await reading; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: intermediate batches append without flushing and the final batch is durable")]
    [InlineData(65)]
    [InlineData(128)]
    [InlineData(129)]
    public async Task ExecuteAsync_MultipleBatches_ShouldFlushOnlyTheLast(int count)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("ctas-flushes", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session, timeout.Token);
        if (count > 65)
        { await ExecAsync(session, "INSERT INTO src VALUES " + string.Join(',', Enumerable.Range(66, count - 65).Select(n => $"({n})")), timeout.Token); }
        int flushes = strategy.JournalFlushes("db");
        int copied = 0;
        SqlPlanExecutor.TableAsStage = (stage, _) =>
        {
            if (stage != "Copied") { return; }
            copied++;
            // Observe actual data-journal flush calls, independent of the executor's arguments.
            (strategy.JournalFlushes("db") - flushes).ShouldBe(copied == (count + 63) / 64 ? 1 : 0);
        };
        try
        {
            // Act
            await ExecAsync(session, "CREATE TABLE copy AS SELECT * FROM src", timeout.Token);
        }
        finally { SqlPlanExecutor.TableAsStage = null; }

        // Assert
        copied.ShouldBe((count + 63) / 64);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT * FROM copy", timeout.Token)).Count.ShouldBe(count);
    }
}
