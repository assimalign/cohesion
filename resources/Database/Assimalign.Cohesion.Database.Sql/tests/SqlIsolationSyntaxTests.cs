using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL isolation configuration")]
public sealed class SqlIsolationSyntaxTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Isolation: BEGIN and SET execute the requested visibility with two sessions")]
    [InlineData("READ COMMITTED", true)]
    [InlineData("READ UNCOMMITTED", true)]
    [InlineData("REPEATABLE READ", false)]
    public async Task ExecuteAsync_RequestedLevel_ShouldControlConcurrentVisibility(string level, bool seesCommit)
    {
        // Arrange / Act / Assert
        foreach (string begin in new[] { "BEGIN", "BEGIN TRANSACTION", "BEGIN WORK", "START TRANSACTION", "SET TRANSACTION" })
        {
            await using var engine = SqlDatabaseEngine.Create("isolation-visibility", new());
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var a = await database.CreateSessionAsync(CancellationToken.None);
            await using var b = await database.CreateSessionAsync(CancellationToken.None);
            await SuccessAsync(b, "CREATE TABLE t(id INT)");
            await SuccessAsync(b, "INSERT INTO t VALUES(1)");
            if (begin == "SET TRANSACTION") { await SuccessAsync(a, "BEGIN"); }
            await SuccessAsync(a, begin + " ISOLATION LEVEL " + level);
            var transaction = a.CurrentTransaction.ShouldNotBeNull();
            transaction.Context.IsolationLevel.ShouldBe(seesCommit ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot);
            (await CountAsync(a)).ShouldBe(1);
            await SuccessAsync(b, "INSERT INTO t VALUES(2)");
            (await CountAsync(a)).ShouldBe(seesCommit ? 2 : 1);
            await SuccessAsync(a, "ROLLBACK");
            database.Coordinator.PruneBound.ShouldBeGreaterThan(transaction.Context.Sequence);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Isolation: repeated early SET retains the original begin snapshot and identity")]
    public async Task ExecuteAsync_RepeatedEarlySet_ShouldKeepBeginSnapshotAndIdentity()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-begin-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var a = await database.CreateSessionAsync(CancellationToken.None);
        await using var b = await database.CreateSessionAsync(CancellationToken.None);
        await SuccessAsync(b, "CREATE TABLE t(id INT)");
        await SuccessAsync(b, "INSERT INTO t VALUES(1)");
        await using var earlierWriter = await b.BeginTransactionAsync(CancellationToken.None);
        await SuccessAsync(b, "DELETE FROM t WHERE id=1");
        await SuccessAsync(a, "BEGIN ISOLATION LEVEL READ COMMITTED");
        var transaction = a.CurrentTransaction.ShouldNotBeNull();
        var original = transaction.Context;

        // Act
        await earlierWriter.CommitAsync(CancellationToken.None);
        await SuccessAsync(b, "INSERT INTO t VALUES(2)");
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await SuccessAsync(a, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, ISOLATION LEVEL READ COMMITTED");
        await SuccessAsync(a, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");

        // Assert
        a.CurrentTransaction.ShouldBeSameAs(transaction);
        transaction.Context.ShouldNotBeSameAs(original);
        transaction.Id.ShouldBe(original.Id);
        transaction.Context.Sequence.ShouldBe(original.Sequence);
        transaction.Context.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
        (await CountAsync(a)).ShouldBe(1);
        await SuccessAsync(a, "COMMIT");
        original.State.ShouldBe(TransactionState.Committed);
        transaction.Context.State.ShouldBe(TransactionState.Committed);
        database.Coordinator.PruneBound.ShouldBeGreaterThan(original.Sequence);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Isolation: every admitted data statement closes SET, including errors and DDL refusal")]
    [InlineData("SELECT 1")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("INSERT INTO t VALUES(1)")]
    [InlineData("SELECT 1/0")]
    [InlineData("SELECT missing FROM t")]
    [InlineData("CREATE TABLE forbidden(id INT)")]
    [InlineData("UPDATE INFORMATION_SCHEMA.TABLES SET TABLE_NAME='forbidden'")]
    public async Task ExecuteAsync_AfterFirstDataStatement_ShouldRefuseSetAndKeepTransaction(string statement)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-late-set", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SuccessAsync(session, "CREATE TABLE t(id INT)");
        await SuccessAsync(session, "BEGIN");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();

        // Act
        try { await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None); }
        catch (DatabaseException) { /* The attempted statement still closes configuration. */ }
        var failed = await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", cancellationToken: CancellationToken.None);

        // Assert: even an idempotent SET is late under the owner rule.
        failed.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04072");
        transaction.State.ShouldBe(TransactionState.Active);
        await SuccessAsync(session, "INSERT INTO t VALUES(2)");
        await SuccessAsync(session, "COMMIT");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Isolation: failures and cancellation leave early configuration and the transaction usable")]
    public async Task ExecuteAsync_FailedSet_ShouldPreserveEarlyTransaction()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-refusals", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        (await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", cancellationToken: CancellationToken.None))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04071");
        (await session.ExecuteAsync("BEGIN ISOLATION LEVEL SERIALIZABLE", cancellationToken: CancellationToken.None))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04073");
        session.CurrentTransaction.ShouldBeNull();
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        foreach (string mode in new[] { "READ ONLY", "READ WRITE", "DEFERRABLE", "NOT DEFERRABLE" })
        {
            var failure = await Should.ThrowAsync<DatabaseParseException>(async () =>
                await session.ExecuteAsync("SET TRANSACTION " + mode, cancellationToken: CancellationToken.None));
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
        }
        (await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", cancellationToken: CancellationToken.None))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04073");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ", cancellationToken: canceled.Token));
        transaction.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        await SuccessAsync(session, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");
        transaction.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
        await transaction.RollbackAsync(CancellationToken.None);
        database.Coordinator.PruneBound.ShouldBeGreaterThan(transaction.Context.Sequence);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Isolation: commit rollback transaction disposal and session disposal release the configuration horizon")]
    [InlineData("commit")][InlineData("rollback")][InlineData("transaction")][InlineData("session")]
    public async Task ExecuteAsync_EndWithoutData_ShouldReleaseConfigurationPin(string end)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("isolation-end", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SuccessAsync(session, "BEGIN ISOLATION LEVEL READ COMMITTED");
        await SuccessAsync(session, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();

        // Act
        if (end == "commit") { await transaction.CommitAsync(CancellationToken.None); }
        else if (end == "rollback") { await transaction.RollbackAsync(CancellationToken.None); }
        else if (end == "transaction") { await transaction.DisposeAsync(); }
        else { await session.DisposeAsync(); }

        // Assert
        transaction.State.ShouldBe(end == "commit" ? TransactionState.Committed : TransactionState.RolledBack);
        database.Coordinator.PruneBound.ShouldBeGreaterThan(transaction.Context.Sequence);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Isolation: racing configuration and end never leaks a context or resurrects a transaction")]
    public async Task ExecuteAsync_ConfigurationRacesEnd_ShouldReleaseEveryHorizon()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("isolation-race", new());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        int configured = 0, refused = 0, ended = 0, races = 0;
        var elapsed = Stopwatch.StartNew();

        // Act: release both poised contenders together. Counters
        // prove both outcomes and every end path; elapsed time only sets a minimum exercise.
        do
        {
            ceiling.Token.ThrowIfCancellationRequested();
            await using var session = await database.CreateSessionAsync(ceiling.Token);
            var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, ceiling.Token);
            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(2);
            int iteration = races++;
            var configuration = Task.Run(() =>
            {
                ready.Signal();
                start.Wait(ceiling.Token);
                if (transaction.TrySetIsolationLevel(IsolationLevel.Snapshot)) { Interlocked.Increment(ref configured); }
                else { Interlocked.Increment(ref refused); }
            }, ceiling.Token);
            var ending = Task.Run(async () =>
            {
                ready.Signal();
                start.Wait(ceiling.Token);
                if (iteration % 4 == 0) { await transaction.CommitAsync(ceiling.Token); }
                else if (iteration % 4 == 1) { await transaction.RollbackAsync(ceiling.Token); }
                else if (iteration % 4 == 2) { await transaction.DisposeAsync(); }
                else { await session.DisposeAsync(); }
                Interlocked.Increment(ref ended);
            }, ceiling.Token);
            ready.Wait(ceiling.Token);
            start.Set();
            await Task.WhenAll(configuration, ending).WaitAsync(ceiling.Token);
            transaction.State.ShouldNotBe(TransactionState.Active);
            database.Coordinator.PruneBound.ShouldBeGreaterThan(transaction.Context.Sequence);
        } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || configured == 0 || refused == 0 || races < 16);

        // Assert
        configured.ShouldBeGreaterThan(0);
        refused.ShouldBeGreaterThan(0);
        ended.ShouldBe(races);
        (configured + refused).ShouldBe(races);
    }

    private static async Task SuccessAsync(SqlDatabaseSession session, string sql)
        => (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<int> CountAsync(SqlDatabaseSession session)
    {
        await using var result = (await session.ExecuteAsync("SELECT id FROM t", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        int count = 0;
        await foreach (var _ in result.GetRowsAsync(CancellationToken.None)) { count++; }
        return count;
    }
}
