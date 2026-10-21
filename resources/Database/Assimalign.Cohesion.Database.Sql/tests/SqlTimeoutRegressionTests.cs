using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlTimeoutRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Definition DDL: cancellation after self-commit preserves success and persisted definitions")]
    [InlineData("CREATE FUNCTION plus_one(x INT) RETURNS INT RETURN x + 1", false)]
    [InlineData("DROP FUNCTION plus_one(INT)", false)]
    [InlineData("CREATE DOMAIN positive AS INT CHECK (VALUE > 0)", false)]
    [InlineData("DROP DOMAIN positive", false)]
    [InlineData("CREATE FUNCTION plus_one(x INT) RETURNS INT RETURN x + 1", true)]
    [InlineData("DROP FUNCTION plus_one(INT)", true)]
    [InlineData("CREATE DOMAIN positive AS INT CHECK (VALUE > 0)", true)]
    [InlineData("DROP DOMAIN positive", true)]
    public async Task ExecuteAsync_DefinitionCanceledAfterSelfCommit_ShouldDeliverDurableSuccess(string sql, bool timeout)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
        await using var engine = SqlDatabaseEngine.Create("definition-timeout", Options());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        bool function = sql.Contains("FUNCTION", StringComparison.Ordinal);
        bool drop = sql.StartsWith("DROP", StringComparison.Ordinal);
        if (drop)
        {
            await session.ExecuteAsync(function ? "CREATE FUNCTION plus_one(x INT) RETURNS INT RETURN x + 1"
                : "CREATE DOMAIN positive AS INT CHECK (VALUE > 0)", cancellationToken: ceiling.Token);
        }
        CancellationToken statementToken = default;
        session.StatementTimeout = timeout ? TimeSpan.FromMinutes(1) : TimeSpan.Zero;
        session.StatementTimerArmed = token => statementToken = token;
        int crossed = 0;
        SqlPlanExecutor.AfterSelfCommit = () =>
        {
            crossed++;
            if (timeout)
            {
                session.ExpireStatementTimerForTest();
                statementToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            }
            else { caller.Cancel(); }
        };

        // Act
        DatabaseResult result;
        try { result = await session.ExecuteAsync(sql, cancellationToken: caller.Token); }
        finally { SqlPlanExecutor.AfterSelfCommit = null; }

        // Assert: both the live bindings and the reopened catalog reflect the acknowledged DDL.
        crossed.ShouldBe(1);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        session.LastStatementMetrics!.SelfCommits.ShouldBe(1);
        session.StatementTimeout = TimeSpan.Zero;
        VerifyDefinition(database, function, !drop);
        if (function && !drop) { (await ScalarAsync(session, "SELECT plus_one(41)", ceiling.Token)).ShouldBe(42); }
        if (!function && !drop)
        {
            await session.ExecuteAsync("CREATE TABLE uses_domain (v positive)", cancellationToken: ceiling.Token);
            (await session.ExecuteAsync("INSERT INTO uses_domain VALUES (1)", cancellationToken: ceiling.Token)).AffectedCount.ShouldBe(1);
        }
        await session.DisposeAsync();
        await database.DisposeAsync();
        VerifyDefinition(await engine.OpenDatabaseAsync("db", ceiling.Token), function, !drop);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Apply: a deadline during dirty-page eviction rolls back all 600 unindexed mutations")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_DeadlineDuringPhysicalApply_ShouldRestoreAllRows(bool update, bool insertPhase)
    {
        // Arrange: checkpoint leaves scans clean; only physical apply can dirty and evict a page.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        var options = Options(strategy);
        options.BufferPoolCapacity = 128 * Page.Size;
        await using var engine = SqlDatabaseEngine.Create("apply-timeout", options);
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        await session.ExecuteAsync("CREATE TABLE items (id INT, v INT, padding VARCHAR(5000))", cancellationToken: ceiling.Token);
        for (int first = 0; first < 600; first += 100)
        {
            var sql = new StringBuilder("INSERT INTO items VALUES ");
            for (int row = first; row < first + 100; row++)
            {
                if (row != first) { sql.Append(','); }
                sql.Append(CultureInfo.InvariantCulture, $"({row},0,'{new string('x', 4000)}')");
            }
            await session.ExecuteAsync(sql.ToString(), cancellationToken: ceiling.Token);
        }
        database.CheckpointDataStorage(ceiling.Token);
        long firstApplyPage = insertPhase ? strategy.Capture("db").Data.Length : Page.Size;
        CancellationToken statementToken = default;
        session.StatementTimerArmed = token => statementToken = token;
        // Arm at apply's first eviction so preparation can take arbitrarily long on a slow host.
        session.StatementTimeout = TimeSpan.FromMinutes(1);
        int evictions = 0;
        strategy.BeforeDataWrite = offset =>
        {
            if (offset < firstApplyPage || Interlocked.Increment(ref evictions) != 1) { return; }
            session.ExpireStatementTimerForTest();
            statementToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };

        // Act
        SqlTimeoutException failure;
        try
        {
            failure = await Should.ThrowAsync<SqlTimeoutException>(async () => await session.ExecuteAsync(update
                ? "UPDATE items SET v = 1" : "DELETE FROM items", cancellationToken: ceiling.Token));
        }
        finally { strategy.BeforeDataWrite = null; }

        // Assert: no partial physical effect survives, including after recovery.
        evictions.ShouldBeGreaterThan(0);
        statementToken.IsCancellationRequested.ShouldBeTrue();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE043");
        session.StatementTimeout = TimeSpan.Zero;
        (await ScalarAsync(session, "SELECT COUNT(*) FROM items", ceiling.Token)).ShouldBe(600L);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM items WHERE v = 1", ceiling.Token)).ShouldBe(0L);
        await session.DisposeAsync();
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("db", ceiling.Token);
        await using var reader = await reopened.CreateSessionAsync(ceiling.Token);
        (await ScalarAsync(reader, "SELECT COUNT(*) FROM items", ceiling.Token)).ShouldBe(600L);
        (await ScalarAsync(reader, "SELECT COUNT(*) FROM items WHERE v = 1", ceiling.Token)).ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Index publication: expiration during the durable build leaves no registration and retry succeeds")]
    [InlineData("CREATE INDEX ix ON items(id)", false)]
    [InlineData("ALTER TABLE items ADD CONSTRAINT ix UNIQUE(id)", false)]
    [InlineData("ALTER TABLE items ADD CONSTRAINT ix PRIMARY KEY(id)", false)]
    [InlineData("CREATE INDEX ix ON items(id)", true)]
    [InlineData("ALTER TABLE items ADD CONSTRAINT ix UNIQUE(id)", true)]
    [InlineData("ALTER TABLE items ADD CONSTRAINT ix PRIMARY KEY(id)", true)]
    public async Task ExecuteAsync_TimeoutBeforeIndexPublication_ShouldAllowRetry(string sql, bool deferred)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("publication-timeout", Options());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = deferred ? database.CreateSchemaSession("db", ceiling.Token)
            : await database.CreateSessionAsync(ceiling.Token);
        await session.ExecuteAsync("CREATE TABLE items (id INT)", cancellationToken: ceiling.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES (1),(2)", cancellationToken: ceiling.Token);
        if (deferred) { session.ProvisioningProgress = (_, _) => { }; }
        CancellationToken statementToken = default;
        session.StatementTimerArmed = token => statementToken = token;
        session.StatementTimeout = TimeSpan.FromMinutes(1);
        int flushes = 0;
        FaultInjectingJournalSqlStorageStrategy.BeforeCommitFlush = () =>
        {
            if (++flushes != (deferred ? 2 : 1)) { return; }
            session.ExpireStatementTimerForTest();
            statementToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };

        // Act
        SqlTimeoutException failure;
        try { failure = await Should.ThrowAsync<SqlTimeoutException>(async () => await session.ExecuteAsync(sql, cancellationToken: ceiling.Token)); }
        finally { FaultInjectingJournalSqlStorageStrategy.BeforeCommitFlush = null; }

        // Assert
        flushes.ShouldBeGreaterThan(0);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE043");
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        session.StatementTimeout = TimeSpan.Zero;
        (await session.ExecuteAsync(sql, cancellationToken: ceiling.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
        database.IndexManager.ExportRegistrations().ShouldHaveSingleItem().Definition.Name.ShouldBe("ix");
        (await ScalarAsync(session, "SELECT COUNT(*) FROM items", ceiling.Token)).ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: schema sessions disable both OLTP deadlines during long index builds")]
    public async Task CreateSchemaSession_BuildPastEngineDeadline_ShouldSucceedWithoutOltpTimeouts()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var options = Options();
        options.StatementTimeout = options.LockTimeout = TimeSpan.FromMilliseconds(50);
        await using var engine = SqlDatabaseEngine.Create("provision-timeout", options);
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = database.CreateSchemaSession("db", ceiling.Token);
        await session.ExecuteAsync("CREATE TABLE items (id INT)", cancellationToken: ceiling.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES (1),(2)", cancellationToken: ceiling.Token);
        session.ProvisioningProgress = (_, _) => { };
        int held = 0;
        FaultInjectingJournalSqlStorageStrategy.BeforeCommitFlush = () =>
        {
            if (++held != 1) { return; }
            using var elapsed = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            elapsed.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };

        // Act
        DatabaseResult result;
        try { result = await session.ExecuteAsync("CREATE INDEX ix ON items(id)", cancellationToken: ceiling.Token); }
        finally { FaultInjectingJournalSqlStorageStrategy.BeforeCommitFlush = null; }

        // Assert
        held.ShouldBeGreaterThan(0);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        session.StatementTimeout.ShouldBe(TimeSpan.Zero);
        session.LockTimeout.ShouldBe(TimeSpan.Zero);
        database.IndexManager.ExportRegistrations().ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Lock timeout: the earlier lock deadline keeps SQLSTATE 55P03 after a delayed continuation")]
    public async Task ExecuteAsync_LockTimeoutBeforeDelayedStatementExpiry_ShouldReportLockDeadline()
    {
        // Arrange: event dispatch pauses the wait before its exception reaches the SQL session.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("earlier-deadline", new());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var holder = await database.CreateSessionAsync(ceiling.Token);
        await using var waiter = await database.CreateSessionAsync(ceiling.Token);
        await holder.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, v INT)", cancellationToken: ceiling.Token);
        await holder.ExecuteAsync("INSERT INTO items VALUES (1,0)", cancellationToken: ceiling.Token);
        await using var transaction = await holder.BeginTransactionAsync(ceiling.Token);
        await holder.ExecuteAsync("UPDATE items SET v = 1", cancellationToken: ceiling.Token);
        CancellationToken statementToken = default;
        waiter.StatementTimerArmed = token => statementToken = token;
        waiter.LockTimeout = TimeSpan.FromMilliseconds(50);
        waiter.StatementTimeout = TimeSpan.FromSeconds(1);
        int paused = 0;
        string? outcome = null;
        using var listener = new TimeoutLockListener { Stopped = report =>
        {
            if (report.Payload![0]?.ToString() != database.DataStorage.Name.ToString()) { return; }
            outcome = report.Payload[2]?.ToString();
            Interlocked.Increment(ref paused);
            statementToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        } };

        // Act
        var failure = await Should.ThrowAsync<SqlTimeoutException>(async () =>
            await waiter.ExecuteAsync("UPDATE items SET v = 2", cancellationToken: ceiling.Token));

        // Assert
        paused.ShouldBe(1);
        statementToken.IsCancellationRequested.ShouldBeTrue();
        outcome.ShouldBe("Timeout");
        failure.SqlState.ShouldBe("55P03");
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE044");
    }

    private static SqlDatabaseEngineOptions Options(FaultInjectingJournalSqlStorageStrategy? strategy = null) => new()
    {
        StorageStrategy = strategy ?? new FaultInjectingJournalSqlStorageStrategy(durable: true),
        Durability = StorageCommitDurability.Synchronous,
        CheckpointInterval = TimeSpan.FromHours(1),
        PageWriteBackInterval = TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    };

    private static void VerifyDefinition(SqlDatabase database, bool function, bool present)
    {
        (function ? database.Catalog.Functions.Any(definition => definition.Name == "plus_one")
            : database.Catalog.Domains.Any(definition => definition.Name == "positive")).ShouldBe(present);
    }

    private static async Task<object?> ScalarAsync(SqlDatabaseSession session, string sql, CancellationToken token)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        object? value = null;
        await foreach (var row in result.GetRowsAsync(token)) { value = row.GetValue(0); }
        return value;
    }
}
