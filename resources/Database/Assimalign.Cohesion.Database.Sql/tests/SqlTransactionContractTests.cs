using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL session and transaction on the root bases (concrete-types plan §6.4, "SQL in P4",
/// #1260): the transaction gains the end gate, the repeatable rollback, the <c>Faulted</c> state
/// for a transaction the kernel ended under its caller, and a coded aborted error,
/// <c>COHSQLT005</c>; a commit observes its token only before it starts; the session's teardown
/// ends its transaction with a cause; and the session's "already active" check, its closed-session
/// refusal and its checks' order are the base's. A statement stays statement-atomic: a failed
/// statement never aborts the transaction, so SQL never calls the base's abort. Each test names the
/// behavior it replaces.
/// </summary>
public sealed class SqlTransactionContractTests
{
    private const string AlreadyActive = "A transaction or operation is already active on this session.";

    /// <summary>
    /// A rollback is idempotent for a transaction that did not commit, and refused for one that
    /// did; a commit after a rollback is refused by state. Before the base the model refused a
    /// second rollback ("Cannot rollback transaction in state 'RolledBack'.") and named the state in
    /// its own words ("Cannot commit transaction in state …").
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Transaction: repeated rollback is a no-op; a committed transaction refuses one, and a rolled-back one a commit")]
    public async Task RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var rolledBack = await session.BeginTransactionAsync(TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (10, 1)", cancellationToken: TestTimeout.Token());
        await rolledBack.RollbackAsync(TestTimeout.Token());
        var committed = await session.BeginTransactionAsync(TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (11, 1)", cancellationToken: TestTimeout.Token());
        await committed.CommitAsync(TestTimeout.Token());

        // Act
        await rolledBack.RollbackAsync(TestTimeout.Token());
        await rolledBack.DisposeAsync();
        var rollbackOfCommitted = await Should.ThrowAsync<DatabaseException>(async () => await committed.RollbackAsync(TestTimeout.Token()));
        var commitOfRolledBack = await Should.ThrowAsync<DatabaseException>(async () => await rolledBack.CommitAsync(TestTimeout.Token()));

        // Assert
        rollbackOfCommitted.Message.ShouldBe("The transaction is Committed; a committed transaction cannot roll back.");
        commitOfRolledBack.Message.ShouldBe("The transaction is RolledBack.");
        rolledBack.State.ShouldBe(TransactionState.RolledBack);
        committed.State.ShouldBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
        (await IdsAsync(session)).ShouldBe([1, 11]);
    }

    /// <summary>
    /// BEGIN on a session whose transaction is active is refused with the root base's one message,
    /// for the model's former "A transaction is already active on this session.", and the base checks
    /// it before the model's isolation-level refusal (BEGIN's refusal order): a Serializable BEGIN
    /// fails the "already active" way while a transaction is open, where the model refused the
    /// isolation level first, and the Serializable way only when none is.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Session: BEGIN while a transaction is active is refused with one message, before the isolation-level refusal")]
    public async Task BeginTransactionAsync_WhileActive_ShouldBeRefusedBeforeTheIsolationLevel()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());

        // Act
        var again = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(TestTimeout.Token()));
        var serializable = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, TestTimeout.Token()));
        var text = await session.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token());
        await transaction.RollbackAsync(TestTimeout.Token());
        var unsupported = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, TestTimeout.Token()));

        // Assert: the text BEGIN keeps its state-misuse diagnostic.
        again.Message.ShouldBe(AlreadyActive);
        serializable.Message.ShouldBe(AlreadyActive);
        text.Status.ShouldBe(QueryResultStatus.Error);
        text.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem().Code.ShouldBe("COHSQLT001");
        unsupported.Message.ShouldStartWith("IsolationLevel.Serializable is not supported by the SQL engine yet", Case.Sensitive);
        session.CurrentTransaction.ShouldBeNull();
    }

    /// <summary>
    /// A closed session refuses BEGIN and both execute seams with the root base's message, for the
    /// model's former "Session is not open. Current state: Closed.", and before the isolation-level
    /// refusal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Session: a closed session refuses BEGIN and statements with one message")]
    public async Task BeginAndExecute_OnClosedSession_ShouldRefuseWithOneMessage()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        var session = await database.CreateSessionAsync(TestTimeout.Token());
        await session.DisposeAsync();

        // Act
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, TestTimeout.Token()));
        var typed = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT id FROM t"), TestTimeout.Token()));
        var text = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id FROM t", cancellationToken: TestTimeout.Token()));

        // Assert
        begin.Message.ShouldBe("The session is closed.");
        typed.Message.ShouldBe("The session is closed.");
        text.Message.ShouldBe("The session is closed.");
        session.State.ShouldBe(SessionState.Closed);
    }

    /// <summary>
    /// A transaction the kernel ended under its caller (its database was dropped while the session
    /// held it) reports <see cref="TransactionState.Faulted"/>, stays the session's transaction, and
    /// refuses statements, BEGIN and COMMIT with SQL's coded aborted error, <c>COHSQLT005</c>, until
    /// the caller rolls it back. The root bases order that refusal against a canceled token: both
    /// execute seams check the token first, and BEGIN refuses the open transaction before it checks
    /// the token. Before the bases the model popped the transaction from the session as soon as its
    /// kernel state left Active, so a statement or a BEGIN ran in auto-commit (here against the
    /// dropped database's disposed coordinator), and it refused a COMMIT with "Cannot commit
    /// transaction in state 'Faulted'.". The state itself reads Faulted either way for this path:
    /// the manager that closed under the transaction ended its context as Faulted
    /// (<c>TransactionManager.cs</c>, its disposal), which the base reports once the caller ended it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Transaction: one the kernel ended is Faulted and refuses work with COHSQLT005 until the caller rolls it back")]
    public async Task ExecuteAsync_TransactionEndedByTheKernel_ShouldBeFaultedAndRefuseWorkWithCohsqlt005()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (20, 1)", cancellationToken: TestTimeout.Token());
        await engine.DropDatabaseAsync(DatabaseName, TestTimeout.Token());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        var state = transaction.State;
        var current = session.CurrentTransaction;
        var canceledText = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("SELECT id FROM t", cancellationToken: canceled.Token));
        var canceledTyped = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT id FROM t"), canceled.Token));
        var canceledBegin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(canceled.Token));
        var text = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id FROM t", cancellationToken: TestTimeout.Token()));
        var ddl = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE u (id INT)", cancellationToken: TestTimeout.Token()));
        var textBegin = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token()));
        await transaction.RollbackAsync(TestTimeout.Token());
        await transaction.RollbackAsync(TestTimeout.Token());
        var afterRollback = await Should.ThrowAsync<ObjectDisposedException>(async () => await session.BeginTransactionAsync(TestTimeout.Token()));

        // Assert
        state.ShouldBe(TransactionState.Faulted);
        current.ShouldBeSameAs(transaction);
        canceledText.CancellationToken.ShouldBe(canceled.Token);
        canceledTyped.CancellationToken.ShouldBe(canceled.Token);
        canceledBegin.Message.ShouldBe("COHSQLT005: The session's transaction is aborted; statements are refused until it is rolled back.");
        text.Message.ShouldBe(canceledBegin.Message);
        ddl.Message.ShouldBe(canceledBegin.Message);
        textBegin.Message.ShouldBe(canceledBegin.Message);
        // Once the caller ended it, the state is the kernel's own: the manager that closed under the
        // transaction ended its context as Faulted.
        transaction.State.ShouldBe(TransactionState.Faulted);
        session.CurrentTransaction.ShouldBeNull();
        afterRollback.ShouldNotBeNull();
    }

    /// <summary>
    /// A COMMIT, typed or as statement text, of a transaction the kernel ended under its caller
    /// ends it and fails with <c>COHSQLT005</c>, committing nothing; a second commit reports the
    /// same. Before the bases the model refused it by the kernel's state ("Cannot commit
    /// transaction in state …"), and the text COMMIT answered "COMMIT requires an open transaction."
    /// because the session had dropped the transaction.
    /// </summary>
    /// <param name="text">True to commit with the <c>COMMIT</c> statement, false through the transaction.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Transaction: COMMIT of one the kernel ended fails with COHSQLT005 and commits nothing")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommitAsync_TransactionEndedByTheKernel_ShouldFailWithCohsqlt005(bool text)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (30, 1)", cancellationToken: TestTimeout.Token());
        await engine.DropDatabaseAsync(DatabaseName, TestTimeout.Token());

        // Act
        var commit = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (text)
            {
                await session.ExecuteAsync("COMMIT", cancellationToken: TestTimeout.Token());
            }
            else
            {
                await transaction.CommitAsync(TestTimeout.Token());
            }
        });
        var again = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(TestTimeout.Token()));

        // Assert
        commit.Message.ShouldBe("COHSQLT005: The session's transaction is aborted and cannot commit; nothing was committed.");
        again.Message.ShouldStartWith("The transaction is ", Case.Sensitive);
        transaction.State.ShouldNotBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
    }

    /// <summary>
    /// A token canceled before a commit or rollback starts leaves the transaction as it was: the
    /// root base observes the token only before an end starts, and never hands it to the
    /// coordinator, where the model passed the commit's token to the coordinator's commit.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Transaction: a canceled token never starts a commit or rollback")]
    public async Task EndAsync_TokenCanceledBeforeStart_ShouldLeaveTheTransactionActive()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (40, 1)", cancellationToken: TestTimeout.Token());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(canceled.Token));
        var state = transaction.State;
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (41, 1)", cancellationToken: TestTimeout.Token());
        await transaction.CommitAsync(TestTimeout.Token());

        // Assert
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await IdsAsync(session)).ShouldBe([1, 40, 41]);
    }

    /// <summary>
    /// Closing a session ends its transaction as the session's teardown: the caller's rollback
    /// afterwards raises nothing, and its commit fails with <c>COHSQLT005</c> naming the closure,
    /// "The session closed before the transaction ended.", and commits nothing. Before the bases the
    /// model's teardown disposed the transaction, and a later commit reported "Cannot commit
    /// transaction in state 'RolledBack'.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Transaction: closing the session ends the transaction; a later rollback is a no-op and a commit fails with COHSQLT005 naming the closure")]
    public async Task DisposeAsync_SessionWithTransaction_ShouldEndItWithTheTeardownCause()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        var session = await database.CreateSessionAsync(TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t (id, val) VALUES (50, 1)", cancellationToken: TestTimeout.Token());

        // Act
        await session.DisposeAsync();
        await transaction.RollbackAsync(TestTimeout.Token());
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(TestTimeout.Token()));

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        commit.Message.ShouldBe("COHSQLT005: The session's transaction is aborted and cannot commit; nothing was committed. Cause: The session closed before the transaction ended.");
        commit.InnerException.ShouldNotBeNull().Message.ShouldBe("The session closed before the transaction ended.");
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync(TestTimeout.Token());
        (await IdsAsync(observer)).ShouldBe([1]);
    }

    /// <summary>
    /// A commit while a statement of the transaction still runs (here one waiting on another
    /// transaction's row lock) is refused with the root base's message and leaves the transaction
    /// active and committable once the statement completes. Before the base the model's commit
    /// raced the running statement into the coordinator.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Transaction: a commit while one of its statements runs is refused and leaves the transaction active")]
    public async Task CommitAsync_WhileAStatementRuns_ShouldBeRefusedAndLeaveTheTransactionActive()
    {
        // Arrange: the holder locks row 1; the waiter's update of it waits for the lock.
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine);
        await using var holder = await database.CreateSessionAsync(TestTimeout.Token());
        await using var waiter = await database.CreateSessionAsync(TestTimeout.Token());
        var holding = await holder.BeginTransactionAsync(TestTimeout.Token());
        await holder.ExecuteAsync("UPDATE t SET val = 2 WHERE id = 1", cancellationToken: TestTimeout.Token());
        var waiting = await waiter.BeginTransactionAsync(TestTimeout.Token());
        var update = waiter.ExecuteAsync("UPDATE t SET val = 3 WHERE id = 1", cancellationToken: TestTimeout.Token()).AsTask();
        await WaitUntilAsync(() => waiting.RunningStatements == 1);

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await waiting.CommitAsync(TestTimeout.Token()));
        var state = waiting.State;
        await holding.RollbackAsync(TestTimeout.Token());
        await update.WaitAsync(TestTimeout.Token());
        await waiting.CommitAsync(TestTimeout.Token());

        // Assert
        refusal.Message.ShouldBe("An operation of the transaction is still running; commit after it completes.");
        state.ShouldBe(TransactionState.Active);
        waiting.State.ShouldBe(TransactionState.Committed);
        (await ValuesAsync(holder)).ShouldBe([3]);
    }

    /// <summary>
    /// On an offline database (#1243) the root bases check before the model: BEGIN on the session
    /// that holds an open transaction is refused "already active", where the model refused it with
    /// <c>COHSQLT004</c>; and a canceled token is refused before the offline database by session
    /// creation, BEGIN, both execute seams and schema provisioning, where the model reported
    /// <c>COHSQLT004</c> first. The transaction's own commit still gets the coded refusal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Offline: the base's checks come before COHSQLT004, a canceled token included")]
    public async Task ExecuteAsync_OnOfflineDatabase_ShouldRunTheBaseChecksFirst()
    {
        // Arrange: a commit's fsync fails and takes the database offline under an open transaction.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "offline-order",
            StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await CreateDatabaseAsync(engine);
        await using var holder = await database.CreateSessionAsync(TestTimeout.Token());
        await using var other = await database.CreateSessionAsync(TestTimeout.Token());
        var open = await holder.BeginTransactionAsync(TestTimeout.Token());
        await holder.ExecuteAsync("INSERT INTO t (id, val) VALUES (60, 1)", cancellationToken: TestTimeout.Token());
        using (FaultInjectingJournalSqlStorageStrategy.FailJournalFlushes(1))
        {
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await other.ExecuteAsync("INSERT INTO t (id, val) VALUES (61, 1)", cancellationToken: TestTimeout.Token()));
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var schema = SqlSchema.Compile(DatabaseName, _ => { });

        // Act
        var beginOnHolder = await Should.ThrowAsync<DatabaseException>(async () => await holder.BeginTransactionAsync(TestTimeout.Token()));
        var canceledSession = await Should.ThrowAsync<OperationCanceledException>(async () => await database.CreateSessionAsync(canceled.Token));
        var canceledBegin = await Should.ThrowAsync<OperationCanceledException>(async () => await other.BeginTransactionAsync(canceled.Token));
        var canceledText = await Should.ThrowAsync<OperationCanceledException>(async () => await other.ExecuteAsync("SELECT id FROM t", cancellationToken: canceled.Token));
        var canceledTyped = await Should.ThrowAsync<OperationCanceledException>(async () => await other.ExecuteAsync(SqlQueryRequest.FromSql("SELECT id FROM t"), canceled.Token));
        var canceledSchema = await Should.ThrowAsync<OperationCanceledException>(async () => await database.ApplySchemaAsync(schema, canceled.Token));
        var begin = await Should.ThrowAsync<DatabaseOfflineException>(async () => await other.BeginTransactionAsync(TestTimeout.Token()));
        var statement = await Should.ThrowAsync<DatabaseOfflineException>(async () => await other.ExecuteAsync("SELECT id FROM t", cancellationToken: TestTimeout.Token()));
        var provisioning = await Should.ThrowAsync<DatabaseOfflineException>(async () => await database.ApplySchemaAsync(schema, TestTimeout.Token()));
        var commit = await Should.ThrowAsync<DatabaseOfflineException>(async () => await open.CommitAsync(TestTimeout.Token()));

        // Assert
        beginOnHolder.Message.ShouldBe(AlreadyActive);
        canceledSession.CancellationToken.ShouldBe(canceled.Token);
        canceledBegin.CancellationToken.ShouldBe(canceled.Token);
        canceledText.CancellationToken.ShouldBe(canceled.Token);
        canceledTyped.CancellationToken.ShouldBe(canceled.Token);
        canceledSchema.CancellationToken.ShouldBe(canceled.Token);
        foreach (var refusal in new[] { begin, statement, provisioning, commit })
        {
            refusal.Code.ShouldBe("COHSQLT004");
        }
    }

    private const string DatabaseName = "contract";

    // Quiet workers: only the statements a test runs write anything.
    private static SqlDatabaseEngine CreateEngine() => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
    {
        EngineName = "contract-engine",
        CheckpointInterval = TimeSpan.FromHours(1),
        PageWriteBackInterval = TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    });

    // A database with one committed row: t (1, 1).
    private static async Task<SqlDatabase> CreateDatabaseAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var setup = await database.CreateSessionAsync(TestTimeout.Token());
        await setup.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, val INT NOT NULL)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO t (id, val) VALUES (1, 1)", cancellationToken: TestTimeout.Token());
        return database;
    }

    private static async Task<List<int>> IdsAsync(SqlDatabaseSession session)
        => await ColumnAsync(session, "SELECT id FROM t ORDER BY id");

    private static async Task<List<int>> ValuesAsync(SqlDatabaseSession session)
        => await ColumnAsync(session, "SELECT val FROM t WHERE id = 1");

    private static async Task<List<int>> ColumnAsync(SqlDatabaseSession session, string sql)
    {
        var values = new List<int>();
        var result = (await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token())).ShouldBeAssignableTo<QueryResultSet>().ShouldNotBeNull();
        await using (result)
        {
            await foreach (var row in result.GetRowsAsync(TestTimeout.Token()))
            {
                values.Add(Convert.ToInt32(row.GetValue(0)));
            }
        }

        return values;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var token = TestTimeout.Token();
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(10, token);
        }
    }
}
