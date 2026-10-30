using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Savepoint engine races")]
public sealed class NamedSavepointTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Named savepoints: duplicates, release scope, repeatable undo and commit (#1726)")]
    public async Task ExecuteAsync_NestedDuplicateScopes_ShouldUndoNewestAndCommitPrefix()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("named-savepoints", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await using var observer = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE COLLECTION items");
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})");
        await ExecuteAsync(session, "SAVEPOINT root");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k2', {'n': 2})");
        await ExecuteAsync(session, "SAVEPOINT duplicate");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k3', {'n': 3})");
        await ExecuteAsync(session, "SAVEPOINT duplicate");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})");

        // Act / Assert
        await ExecuteAsync(session, "ROLLBACK TO duplicate");
        (await CountAsync(session)).ShouldBe(3);
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k5', {'n': 5})");
        await ExecuteAsync(session, "ROLLBACK TO SAVEPOINT duplicate");
        (await CountAsync(session)).ShouldBe(3);
        await ExecuteAsync(session, "RELEASE duplicate");
        await ExecuteAsync(session, "ROLLBACK TO duplicate");
        (await CountAsync(session)).ShouldBe(2);
        await ExecuteAsync(session, "SAVEPOINT child");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k6', {'n': 6})");
        await ExecuteAsync(session, "RELEASE SAVEPOINT root");
        (await CountAsync(session)).ShouldBe(3);
        (await CountAsync(observer)).ShouldBe(0);
        await ExecuteAsync(session, "COMMIT");
        session.CurrentTransaction.ShouldBeNull();
        (await CountAsync(observer)).ShouldBe(3);
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "SAVEPOINT parent");
        await ExecuteAsync(session, "SAVEPOINT child");
        await ExecuteAsync(session, "RELEASE parent");
        var unknown = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO child"));
        unknown.Diagnostics.ShouldContain(item => item.Code == "CK04013");
        await ExecuteAsync(session, "ROLLBACK");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Named savepoints: controls require an explicit transaction (#1726)")]
    [InlineData("SAVEPOINT outside")]
    [InlineData("RELEASE SAVEPOINT outside")]
    [InlineData("ROLLBACK TO outside")]
    public async Task ExecuteAsync_OutsideTransaction_ShouldRefuse(string control)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("savepoint-outside", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(control));

        // Assert
        failure.Diagnostics.ShouldContain(item => item.Code == "CK04012");
        session.CurrentTransaction.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Named savepoints: failed statements preserve the model's recovery rule (#1726)")]
    public async Task ExecuteAsync_FailedStatement_ShouldKeepModelFailureRule()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("savepoint-failure", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE COLLECTION items");
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "SAVEPOINT keep");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})");

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM absent"));

        // Assert
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO keep"));
        refused.Diagnostics.ShouldContain(item => item.Code == "CD04101");
        await ExecuteAsync(session, "ROLLBACK");
        (await CountAsync(session)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Named savepoints: incomplete undo fences reads and commit until retry (#1726)")]
    public async Task ExecuteAsync_IncompleteUndo_ShouldFenceEveryStatementUntilOuterRetry()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("savepoint-fence", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE COLLECTION items");
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "SAVEPOINT outer");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k1', {'n': 1})");
        await ExecuteAsync(session, "SAVEPOINT target");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k2', {'n': 2})");
        await ExecuteAsync(session, "SAVEPOINT newer");
        int injected = 0;
        UndoMarkerDriver.SetBatchObserver(database.Coordinator, () =>
        {
            Interlocked.Increment(ref injected);
            throw new InvalidOperationException("Injected undo observer failure");
        });

        // Act
        try { (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO target"))).Diagnostics[0].Code.ShouldBe("CK04009"); }
        finally { UndoMarkerDriver.SetBatchObserver(database.Coordinator, null); }

        // Assert
        injected.ShouldBe(1);
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
        foreach (string statement in new[] { "SELECT count(*) FROM items", "INSERT INTO items (KEY, VALUE) VALUES ('k3', {'n': 3})", "BEGIN", "COMMIT", "SAVEPOINT x", "RELEASE target", "ROLLBACK TO newer", "SELECT (" })
        {
            var refused = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement));
            refused.Diagnostics.ShouldContain(item => item.Code == "CK04009");
        }
        await ExecuteAsync(session, "ROLLBACK TO outer");
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Active);
        (await CountAsync(session)).ShouldBe(0);
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('k4', {'n': 4})");
        await ExecuteAsync(session, "COMMIT");
        (await CountAsync(session)).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Named savepoints: repeated admitted undo excludes owner reads and drains before root end (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UndoRacesRootEnd_ShouldDrainAndReleaseAdmission(bool dispose)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = DocumentDatabaseEngine.Create("savepoint-end-race", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE COLLECTION items");
        int rounds = 0, overlaps = 0, refusals = 0;
        var elapsed = Stopwatch.StartNew();

        // Act: actual undo progress, rather than a delay, opens each overlap interval.
        do
        {
            await ExecuteAsync(session, "BEGIN");
            var transaction = session.CurrentTransaction.ShouldNotBeNull();
            await ExecuteAsync(session, "SAVEPOINT scope");
            await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('one', {'n': 1})");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            UndoMarkerDriver.SetBatchObserver(database.Coordinator, () =>
            {
                Interlocked.Increment(ref overlaps);
                entered.TrySetResult();
                release.Wait(timeout.Token);
            });
            var undo = DedicatedTestTask.Run(async () => await ExecuteAsync(session, "ROLLBACK TO scope"), timeout.Token);
            Task? end = null;
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                var refused = await Should.ThrowAsync<DatabaseException>(async () => await CountAsync(session));
                refused.Diagnostics.ShouldContain(item => item.Code == "CK04009");
                refusals++;
                end = dispose ? transaction.DisposeAsync().AsTask() : transaction.RollbackAsync(timeout.Token).AsTask();
                end.IsCompleted.ShouldBeFalse("the admitted control owns the end gate until its observer is released");
            }
            finally
            {
                release.Set();
                try { await undo.WaitAsync(timeout.Token); }
                finally
                {
                    UndoMarkerDriver.SetBatchObserver(database.Coordinator, null);
                    if (end is not null) { await end.WaitAsync(timeout.Token); }
                }
            }
            transaction.State.ShouldBe(TransactionState.RolledBack);
            session.CurrentTransaction.ShouldBeNull();
            (await CountAsync(session)).ShouldBe(0);
            rounds++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8 || overlaps < 8 || refusals < 8)
            && elapsed.Elapsed < TimeSpan.FromSeconds(30));

        // Assert
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        overlaps.ShouldBeGreaterThanOrEqualTo(8);
        refusals.ShouldBe(rounds);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Named savepoints: rollback releases scoped locks and wakes a queued owner (#1726)")]
    public async Task ExecuteAsync_RollbackToEmptyScope_ShouldUnblockLockWaiter()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = DocumentDatabaseEngine.Create("savepoint-locks", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await using var waiter = await database.CreateSessionAsync();
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(waiter, "BEGIN");
        await ExecuteAsync(session, "SAVEPOINT scope");
        var locks = database.Coordinator.LockManager;
        var resource = LockResource.Entry(999, 123);
        await locks.AcquireAsync(session.CurrentTransaction!.Context.Sequence, resource, LockMode.Exclusive, timeout.Token);
        var waiting = locks.AcquireAsync(waiter.CurrentTransaction!.Context.Sequence, resource, LockMode.Exclusive, timeout.Token);
        waiting.IsCompleted.ShouldBeFalse();

        // Act
        await ExecuteAsync(session, "ROLLBACK TO scope");
        await waiting.AsTask().WaitAsync(timeout.Token);

        // Assert
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Active);
        await ExecuteAsync(session, "COMMIT");
        await ExecuteAsync(waiter, "ROLLBACK");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Named savepoints: pre-canceled controls preserve names and admitted undo completes (#1726)")]
    [InlineData("SAVEPOINT other")]
    [InlineData("RELEASE scope")]
    [InlineData("ROLLBACK TO scope")]
    public async Task ExecuteAsync_Cancellation_ShouldLeaveScopesUsable(string control)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("savepoint-cancellation", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE COLLECTION items");
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "SAVEPOINT scope");
        await ExecuteAsync(session, "INSERT INTO items (KEY, VALUE) VALUES ('one', {'n': 1})");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(control, cancellationToken: canceled.Token));
        (await CountAsync(session)).ShouldBe(1);
        using var duringUndo = new CancellationTokenSource();
        int observed = 0;
        UndoMarkerDriver.SetBatchObserver(database.Coordinator, () => { observed++; duringUndo.Cancel(); });
        try { (await session.ExecuteAsync("ROLLBACK TO scope", cancellationToken: duringUndo.Token)).Status.ShouldBe(DatabaseResultStatus.Success); }
        finally { UndoMarkerDriver.SetBatchObserver(database.Coordinator, null); }
        observed.ShouldBe(1);
        (await CountAsync(session)).ShouldBe(0);
        await ExecuteAsync(session, "ROLLBACK TO scope");
        await ExecuteAsync(session, "COMMIT");
    }

    private static async Task ExecuteAsync(DocumentDatabaseSession session, string source)
        => (await session.ExecuteAsync(source)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<long> CountAsync(DocumentDatabaseSession session)
    {
        await using var result = (await session.ExecuteAsync("SELECT count(*) FROM items")).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in result.GetRowsAsync()) { return Convert.ToInt64(row.GetValue(0)); }
        throw new InvalidOperationException("COUNT returned no row");
    }
}
