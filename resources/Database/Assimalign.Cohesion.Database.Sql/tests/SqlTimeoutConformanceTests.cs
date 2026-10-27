using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

// Executable cases for DIALECT's server-side timeout contract (#1231).
[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlTimeoutConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: a blocked cascade stops at its deadline, releases new locks and preserves the session and transaction")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DeepCascadeTimeout_ShouldPreserveRowsAndReleaseStatementLocks(bool explicitTransaction)
    {
        // Arrange: the function blocks cascade preparation until the real statement token expires.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var function = new TimeoutProbeFunction();
        var builder = SqlDatabaseEngine.CreateBuilder("cascade-timeout");
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync(ceiling.Token);
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        await session.ExecuteAsync("CREATE TABLE chain (id INT PRIMARY KEY, parent_id INT, CONSTRAINT fk_chain FOREIGN KEY(parent_id) REFERENCES chain(id) ON DELETE CASCADE)", cancellationToken: ceiling.Token);
        await session.ExecuteAsync("CREATE INDEX chain_parent ON chain(parent_id)", cancellationToken: ceiling.Token);
        const int depth = 3;
        await session.ExecuteAsync("INSERT INTO chain VALUES (1,NULL),(2,1),(3,2)", cancellationToken: ceiling.Token);
        await session.ExecuteAsync("CREATE TABLE prior (id INT PRIMARY KEY)", cancellationToken: ceiling.Token);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(ceiling.Token) : null;
        if (transaction is not null) { await session.ExecuteAsync("INSERT INTO prior VALUES (1)", cancellationToken: ceiling.Token); }
        session.StatementTimeout = TimeSpan.FromMilliseconds(80);
        function.Callback = token => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue(); token.ThrowIfCancellationRequested(); };

        // Act: the client has no cancellation deadline near the statement's timer.
        var elapsed = Stopwatch.StartNew();
        var failure = await Should.ThrowAsync<SqlTimeoutException>(async () =>
            await session.ExecuteAsync("DELETE FROM chain WHERE id = 1 AND timeout_probe(id) = 1", cancellationToken: ceiling.Token));
        elapsed.Stop();
        session.StatementTimeout = TimeSpan.Zero;

        // Assert: no physical mutation or lock from the failed statement survives.
        failure.SqlState.ShouldBe("57014");
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05043");
        function.Calls.ShouldBe(1);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        ceiling.IsCancellationRequested.ShouldBeFalse();
        (await ScalarAsync(session, "SELECT COUNT(*) FROM chain", ceiling.Token)).ShouldBe((long)depth);
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Active);
            session.CurrentTransaction.ShouldBeSameAs(transaction);
            (await ScalarAsync(session, "SELECT COUNT(*) FROM prior", ceiling.Token)).ShouldBe(1L);
        }
        await using var probe = await database.CreateSessionAsync(ceiling.Token);
        probe.LockTimeout = TimeSpan.FromMilliseconds(200);
        (await probe.ExecuteAsync("UPDATE chain SET parent_id = parent_id WHERE id = 1", cancellationToken: ceiling.Token)).AffectedCount.ShouldBe(1);
        if (transaction is not null)
        {
            await Should.ThrowAsync<SqlTimeoutException>(async () =>
                await probe.ExecuteAsync("INSERT INTO prior VALUES (1)", cancellationToken: ceiling.Token));
            await transaction.CommitAsync(ceiling.Token);
        }
        (await session.ExecuteAsync("INSERT INTO prior VALUES (2)", cancellationToken: ceiling.Token)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: row and unique-key waits use engine defaults and per-session overrides without aborting explicit transactions")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_LockTimeout_ShouldReleaseNewLocksAndKeepTransaction(bool uniqueKey, bool explicitTransaction)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var options = new SqlDatabaseEngineOptions { LockTimeout = TimeSpan.FromMilliseconds(80) };
        await using var engine = SqlDatabaseEngine.Create("lock-timeout", options);
        options.LockTimeout = TimeSpan.Zero;
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var holder = await database.CreateSessionAsync(ceiling.Token);
        await using var waiter = await database.CreateSessionAsync(ceiling.Token);
        await holder.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, v INT)", cancellationToken: ceiling.Token);
        await holder.ExecuteAsync("INSERT INTO items VALUES (1, 0)", cancellationToken: ceiling.Token);
        await using var held = await holder.BeginTransactionAsync(ceiling.Token);
        await holder.ExecuteAsync(uniqueKey ? "INSERT INTO items VALUES (2, 0)" : "UPDATE items SET v = 1 WHERE id = 1", cancellationToken: ceiling.Token);
        await using var transaction = explicitTransaction ? await waiter.BeginTransactionAsync(ceiling.Token) : null;

        // Act
        var failure = await Should.ThrowAsync<SqlTimeoutException>(async () => await waiter.ExecuteAsync(uniqueKey
            ? "INSERT INTO items VALUES (2, 2)" : "UPDATE items SET v = 2 WHERE id = 1", cancellationToken: ceiling.Token));

        // Assert: failed waits do not strand new grants or end the explicit transaction.
        failure.SqlState.ShouldBe("55P03");
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04044");
        failure.InnerException.ShouldBeOfType<TransactionLockTimeoutException>();
        if (transaction is not null) { transaction.State.ShouldBe(TransactionState.Active); }
        await holder.ExecuteAsync("INSERT INTO items VALUES (3, 3)", cancellationToken: ceiling.Token);
        await held.RollbackAsync(ceiling.Token);
        waiter.LockTimeout = TimeSpan.Zero;
        (await waiter.ExecuteAsync("INSERT INTO items VALUES (4, 4)", cancellationToken: ceiling.Token)).AffectedCount.ShouldBe(1);
        if (transaction is not null) { await transaction.CommitAsync(ceiling.Token); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: linked statement cancellation reaches functions through text and typed requests and overrides restore inheritance")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FunctionTimeout_ShouldUseStatementTokenAndSessionOverride(bool typed)
    {
        // Arrange
        var function = new TimeoutProbeFunction();
        var builder = SqlDatabaseEngine.CreateBuilder("function-timeout");
        builder.Options.StatementTimeout = TimeSpan.FromMilliseconds(80);
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        function.Callback = token => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue(); token.ThrowIfCancellationRequested(); };

        // Act
        var failure = await Should.ThrowAsync<SqlTimeoutException>(async () =>
        {
            if (typed) { await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT timeout_probe(1)"), CancellationToken.None); }
            else { await session.ExecuteAsync("SELECT timeout_probe(1)", cancellationToken: CancellationToken.None); }
        });
        function.Callback = token => token.CanBeCanceled.ShouldBeFalse();
        session.StatementTimeout = TimeSpan.Zero;
        var value = await ScalarAsync(session, "SELECT timeout_probe(2)", CancellationToken.None);
        session.StatementTimeout = null;
        function.Callback = token => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue(); token.ThrowIfCancellationRequested(); };
        var inherited = await Should.ThrowAsync<SqlTimeoutException>(async () =>
            await session.ExecuteAsync("SELECT timeout_probe(3)", cancellationToken: CancellationToken.None));

        // Assert
        failure.SqlState.ShouldBe("57014");
        inherited.SqlState.ShouldBe("57014");
        value.ShouldBe(2);
        function.Calls.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Timeout: caller cancellation keeps its cancellation meaning")]
    public async Task ExecuteAsync_CallerCancellation_ShouldNotBecomeTimeout()
    {
        // Arrange
        using var caller = new CancellationTokenSource();
        var function = new TimeoutProbeFunction { Callback = token => { caller.Cancel(); token.ThrowIfCancellationRequested(); } };
        var builder = SqlDatabaseEngine.CreateBuilder("caller-cancellation");
        builder.Options.StatementTimeout = TimeSpan.FromSeconds(5);
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("SELECT timeout_probe(1)", cancellationToken: caller.Token));

        // Assert
        function.Calls.ShouldBe(1);
        (await ScalarAsync(session, "SELECT 2", CancellationToken.None)).ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: ordinary caller cancellation retains explicit transaction locks with timers off or on")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CallerCancellationDuringApply_ShouldPreserveExistingLockingContract(bool timerEnabled)
    {
        // Arrange: caller cancellation has always retained ordinary DML grants until transaction
        // end. Server-timeout cleanup must not broaden that behavior change to client cancellation.
        using var caller = new CancellationTokenSource();
        var function = new TimeoutProbeFunction { Callback = token => { caller.Cancel(); token.ThrowIfCancellationRequested(); } };
        var builder = SqlDatabaseEngine.CreateBuilder("caller-locks");
        builder.Options.StatementTimeout = timerEnabled ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("INSERT INTO items VALUES (1) RETURNING timeout_probe(id)", cancellationToken: caller.Token));

        // Assert: the physical statement rolled back, but its ordinary transaction-duration
        // grants remain. A rollback then releases them and a neighboring session can write.
        (await ScalarAsync(session, "SELECT COUNT(*) FROM items", CancellationToken.None)).ShouldBe(0L);
        transaction.State.ShouldBe(TransactionState.Active);
        await using var probe = await database.CreateSessionAsync(CancellationToken.None);
        probe.StatementTimeout = TimeSpan.Zero;
        probe.LockTimeout = TimeSpan.FromMilliseconds(80);
        var blocked = await Should.ThrowAsync<SqlTimeoutException>(async () =>
            await probe.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: CancellationToken.None));
        blocked.SqlState.ShouldBe("55P03");
        await transaction.RollbackAsync(CancellationToken.None);
        (await probe.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: cancellation inside RETURNING restores the whole physical statement and its new locks")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TimeoutDuringApply_ShouldRollBackPhysicalBracket(bool explicitTransaction)
    {
        // Arrange: RETURNING runs after insertion, while the statement's physical bracket is open.
        var function = new TimeoutProbeFunction();
        var builder = SqlDatabaseEngine.CreateBuilder("apply-timeout");
        builder.Functions.Add(function);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        session.StatementTimeout = TimeSpan.FromMilliseconds(80);
        function.Callback = token => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue(); token.ThrowIfCancellationRequested(); };

        // Act
        var failure = await Should.ThrowAsync<SqlTimeoutException>(async () =>
            await session.ExecuteAsync("INSERT INTO items VALUES (1), (2) RETURNING timeout_probe(id)", cancellationToken: CancellationToken.None));

        // Assert
        failure.SqlState.ShouldBe("57014");
        function.Calls.ShouldBe(1);
        session.StatementTimeout = TimeSpan.Zero;
        (await ScalarAsync(session, "SELECT COUNT(*) FROM items", CancellationToken.None)).ShouldBe(0L);
        await using var probe = await database.CreateSessionAsync(CancellationToken.None);
        probe.LockTimeout = TimeSpan.FromMilliseconds(200);
        (await probe.ExecuteAsync("INSERT INTO items VALUES (1), (2)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Active);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await ScalarAsync(probe, "SELECT COUNT(*) FROM items", CancellationToken.None)).ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Timeout: the statement deadline can stop a lock wait before its lock deadline")]
    public async Task ExecuteAsync_StatementDeadlineWhileWaiting_ShouldReportStatementTimeout()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var engine = SqlDatabaseEngine.Create("statement-lock-wait", new());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var holder = await database.CreateSessionAsync(ceiling.Token);
        await using var waiter = await database.CreateSessionAsync(ceiling.Token);
        await holder.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: ceiling.Token);
        await using var transaction = await holder.BeginTransactionAsync(ceiling.Token);
        await holder.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: ceiling.Token);
        waiter.StatementTimeout = TimeSpan.FromMilliseconds(80);
        waiter.LockTimeout = TimeSpan.FromSeconds(5);

        // Act
        var failure = await Should.ThrowAsync<SqlTimeoutException>(async () =>
            await waiter.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: ceiling.Token));

        // Assert
        failure.SqlState.ShouldBe("57014");
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05043");
        await transaction.RollbackAsync(ceiling.Token);
        (await waiter.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: ceiling.Token)).AffectedCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Timeout: a zero session override disables an inherited lock timeout while another session retains its own budget")]
    public async Task ExecuteAsync_DisabledLockOverride_ShouldWaitPastEngineDefault()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var engine = SqlDatabaseEngine.Create("disabled-lock-timeout", new() { LockTimeout = TimeSpan.FromMilliseconds(50) });
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var holder = await database.CreateSessionAsync(ceiling.Token);
        await using var waiter = await database.CreateSessionAsync(ceiling.Token);
        await using var timed = await database.CreateSessionAsync(ceiling.Token);
        await holder.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: ceiling.Token);
        await using var transaction = await holder.BeginTransactionAsync(ceiling.Token);
        await holder.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: ceiling.Token);
        waiter.LockTimeout = TimeSpan.Zero;
        waiter.StatementTimeout = TimeSpan.FromSeconds(5);
        var pending = waiter.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: ceiling.Token).AsTask();

        // Act: a neighboring session's real timer demonstrates that the inherited budget elapsed.
        try
        {
            var failure = await Should.ThrowAsync<SqlTimeoutException>(async () =>
                await timed.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: ceiling.Token));
            failure.SqlState.ShouldBe("55P03");
            pending.IsCompleted.ShouldBeFalse();
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); }

        // Assert
        (await pending.WaitAsync(ceiling.Token)).AffectedCount.ShouldBe(1);
        waiter.LockTimeout = null;
        waiter.LockTimeout.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: negotiated wire diagnostics preserve the code, connection and explicit transaction")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TimeoutOverWire_ShouldKeepConnectionAndTransaction(bool lockTimeout)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(configureEngine: options =>
        {
            options.StatementTimeout = lockTimeout ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1);
            options.LockTimeout = lockTimeout ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromSeconds(5);
        });
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var holder = await database!.CreateSessionAsync(TestTimeout.Token());
        await using var transaction = await holder.BeginTransactionAsync(TestTimeout.Token());
        await holder.ExecuteAsync("UPDATE users SET name = 'held' WHERE id = 1", cancellationToken: TestTimeout.Token());
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "timeouts", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        await connection.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync("INSERT INTO users VALUES (3, 'prior')", cancellationToken: TestTimeout.Token());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("UPDATE users SET name = 'waited' WHERE id = 1", cancellationToken: TestTimeout.Token()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(lockTimeout ? "CS04044" : "CS05043");
        await connection.ExecuteAsync("COMMIT", cancellationToken: TestTimeout.Token());
        (await connection.QueryAsync("SELECT id FROM users WHERE id = 3", cancellationToken: TestTimeout.Token())).ShouldHaveSingleItem()[0].ShouldBe(3);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: expiration after execution or a catalog self-commit cannot turn durable success into failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TimeoutAfterExecutionBoundary_ShouldDeliverSuccess(bool ddl)
    {
        // Arrange: wait for the real timer after the final apply/catalog commit. These hooks
        // are already the #1246 proof seam; no timing guess controls which side of commit runs.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = true };
        await using var engine = SqlDatabaseEngine.Create("late-timeout", new()
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Synchronous,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        if (!ddl) { await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: ceiling.Token); }
        session.StatementTimeout = TimeSpan.FromMilliseconds(80);
        int crossed = 0;
        CancellationToken statementToken = default;
        session.StatementTimerArmed = token => statementToken = token;
        void CrossBoundary()
        {
            crossed++;
            statementToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            statementToken.IsCancellationRequested.ShouldBeTrue();
        }
        if (ddl) { SqlPlanExecutor.AfterSelfCommit = CrossBoundary; }
        else { SqlPlanExecutor.AfterInsertApplied = CrossBoundary; }

        // Act
        DatabaseResult result;
        try { result = await session.ExecuteAsync(ddl ? "CREATE TABLE items (id INT PRIMARY KEY)" : "INSERT INTO items VALUES (42)", cancellationToken: ceiling.Token); }
        finally { SqlPlanExecutor.AfterSelfCommit = null; SqlPlanExecutor.AfterInsertApplied = null; }

        // Assert: the acknowledged result survives a reopen.
        crossed.ShouldBe(1);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        session.StatementTimeout = TimeSpan.Zero;
        await session.DisposeAsync();
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("db", ceiling.Token);
        await using var reader = await reopened.CreateSessionAsync(ceiling.Token);
        reader.StatementTimeout = TimeSpan.Zero;
        (await ScalarAsync(reader, "SELECT COUNT(*) FROM items", ceiling.Token)).ShouldBe(ddl ? 0L : 1L);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Timeout: engine and session reject unsupported timeout ranges")]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public async Task Create_InvalidTimeout_ShouldRefuseBeforeExecution(long milliseconds)
    {
        // Arrange
        var invalid = TimeSpan.FromMilliseconds(milliseconds);

        // Act
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("invalid", new() { StatementTimeout = invalid }));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("invalid", new() { LockTimeout = invalid }));
        await using var engine = SqlDatabaseEngine.Create("valid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        Should.Throw<ArgumentOutOfRangeException>(() => session.StatementTimeout = invalid);
        Should.Throw<ArgumentOutOfRangeException>(() => session.LockTimeout = invalid);
        // Assert
        session.StatementTimeout.ShouldBeNull();
        session.LockTimeout.ShouldBeNull();
        engine.EngineOptions.StatementTimeout.ShouldBe(TimeSpan.Zero);
        engine.EngineOptions.LockTimeout.ShouldBe(TimeSpan.Zero);
    }

    private static async Task<object?> ScalarAsync(SqlDatabaseSession session, string text, CancellationToken token)
    {
        await using var result = (await session.ExecuteAsync(text, cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        object? value = null;
        await foreach (var row in result.GetRowsAsync(token)) { value = row.GetValue(0); }
        return value;
    }
}
