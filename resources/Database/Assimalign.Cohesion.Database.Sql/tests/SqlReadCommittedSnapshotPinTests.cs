using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The snapshot pin of a statement in a read-committed transaction (#1363). The statement reads
/// through the snapshot captured when it started, and that snapshot can keep a floor below every
/// active sequence: a writer that began before the transaction was still in flight then. A
/// read-committed transaction holds the version purge's bound only at its own sequence, so once
/// that writer committed, a purge pass reclaimed the versions it tombstoned while the statement
/// still had to read them, and the statement returned fewer rows than its snapshot holds. The
/// session now begins a snapshot transaction before the statement captures its snapshot and ends
/// it with the statement, as the Documents, Graph and Blob operations do; the statement itself
/// still runs under the transaction's own context.
/// </summary>
public sealed class SqlReadCommittedSnapshotPinTests
{
    private const string Join = "SELECT a.id, b.id FROM a JOIN b ON a.id = b.id ORDER BY a.id";

    private readonly ITestOutputHelper _output;

    public SqlReadCommittedSnapshotPinTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A join takes its table intent locks after the statement captured its snapshot, so a table
    /// lock holds the statement there while the older writer commits and the purge runs. Before the
    /// fix the pass reclaimed the deleted row and the join returned two rows.
    /// </summary>
    /// <param name="indexed">Whether the inner table has an index on the join key (an index join).</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Read committed: A statement keeps the rows its snapshot sees while an older writer commits and the purge runs (#1363)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_WaitsWhileAnOlderWriterCommitsAndThePurgeRuns_ShouldReturnEveryRowItsSnapshotSees(bool indexed)
    {
        // Arrange: the writer deletes b's row 2 and stays in flight; the reader's read-committed
        // transaction begins after it.
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine, indexed);
        var coordinator = database.Coordinator;
        await using var writerSession = await database.CreateSessionAsync();
        await using var readerSession = await database.CreateSessionAsync();
        var writer = await writerSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await writerSession.ExecuteAsync("DELETE FROM b WHERE id = 2", cancellationToken: TestTimeout.Token());
        var reader = await readerSession.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());

        // A read-committed context holds a's table lock: it adds only its own sequence to the
        // prune bound, so nothing but the statement's own pin can keep the deleted row.
        var blocker = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());
        await coordinator.LockManager.AcquireAsync(blocker.Sequence, LockResource.Object(TableId(database, "a")), LockMode.Exclusive, TestTimeout.Token());
        int openBefore = coordinator.GetOpenContexts().Count;
        var pending = readerSession.ExecuteAsync(Join, cancellationToken: TestTimeout.Token()).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        int openWhileWaiting = coordinator.GetOpenContexts().Count;

        // Act: the writer commits and a purge pass runs while the statement waits for the lock.
        await writer.CommitAsync(TestTimeout.Token());
        long prunedWhileWaiting = coordinator.RunVersionPurgePass(CancellationToken.None);
        await coordinator.RollbackAsync(blocker, CancellationToken.None);
        var rows = await ReadRowsAsync(await pending);
        int openAfter = coordinator.GetOpenContexts().Count;
        long prunedAfterTheStatement = coordinator.RunVersionPurgePass(CancellationToken.None);
        var refreshed = await RowsAsync(readerSession, Join);
        await reader.CommitAsync(TestTimeout.Token());

        // Assert: the statement saw the writer in flight, so it returned row 2; its pin held the
        // version until it ended (the writer, the lock holder and the pin have all ended since),
        // and the transaction, still active, did not hold it after that.
        rows.ShouldBe(["1,1", "2,2", "3,3"]);
        prunedWhileWaiting.ShouldBe(0);
        openWhileWaiting.ShouldBe(openBefore + 1);
        openAfter.ShouldBe(openBefore - 2);
        prunedAfterTheStatement.ShouldBe(1);
        refreshed.ShouldBe(["1,1", "3,3"]);
    }

    /// <summary>
    /// The pin only holds the purge back: the statement still runs under the transaction's own
    /// context. A read-committed cascade that waited for a child writer reaches the child version
    /// that writer committed, which is newer than the statement's snapshot, and removes that
    /// version's index entries through the transaction's context, whose snapshot is captured afresh.
    /// When the session handed the statement a pinned view as its transaction, the index delete
    /// matched nothing through the view's older snapshot and left the deleted child's primary-key
    /// entry live, so the child's key could never be inserted again (#1363 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Read committed: A cascade that waited for a child writer removes the child's index entries (#1363)")]
    public async Task Cascade_WaitsForAChildWriter_ShouldRemoveTheChildsIndexEntries()
    {
        // Arrange: the child writer holds a shared lock on the parent row its child references, and
        // the deleter's read-committed transaction begins while it is in flight.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("db");
        await using var setup = await database.CreateSessionAsync();
        await using var writerSession = await database.CreateSessionAsync();
        await using var deleterSession = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (1)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (10, 1)", cancellationToken: TestTimeout.Token());
        var writer = await writerSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await writerSession.ExecuteAsync("UPDATE c SET pid = pid WHERE id = 10", cancellationToken: TestTimeout.Token());
        var deleter = await deleterSession.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());

        // Act: the cascade waits for the parent row while the child writer commits.
        var pending = deleterSession.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: TestTimeout.Token()).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await writer.CommitAsync(TestTimeout.Token());
        var deleted = await pending;
        await deleter.CommitAsync(TestTimeout.Token());
        var children = await RowsAsync(setup, "SELECT id FROM c");
        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (1)", cancellationToken: TestTimeout.Token());
        var reinserted = await setup.ExecuteAsync("INSERT INTO c (id, pid) VALUES (10, 1)", cancellationToken: TestTimeout.Token());

        // Assert: the cascade deleted the child and its primary-key entry, so the key is free again.
        deleted.AffectedCount.ShouldBe(1);
        children.ShouldBeEmpty();
        reinserted.AffectedCount.ShouldBe(1);
        (await RowsAsync(setup, "SELECT id, pid FROM c")).ShouldBe(["10,1"]);
    }

    /// <summary>
    /// The pin ends with the statement whether it completes, fails or is canceled, and only a
    /// read-committed statement begins one.
    /// </summary>
    /// <param name="isolationLevel">The explicit transaction's isolation level.</param>
    /// <param name="outcome">How the statement ends.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Read committed: A statement's snapshot pin ends with the statement on every path (#1363)")]
    [InlineData(IsolationLevel.ReadCommitted, "completes")]
    [InlineData(IsolationLevel.ReadCommitted, "fails")]
    [InlineData(IsolationLevel.ReadCommitted, "canceled")]
    [InlineData(IsolationLevel.Snapshot, "completes")]
    [InlineData(IsolationLevel.Snapshot, "canceled")]
    public async Task Statement_EndsAnyWay_ShouldEndItsSnapshotPin(IsolationLevel isolationLevel, string outcome)
    {
        // Arrange: a's table lock holds the statement at its intent lock, after any pin began.
        await using var engine = CreateEngine();
        var database = await CreateDatabaseAsync(engine, indexed: false);
        var coordinator = database.Coordinator;
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(isolationLevel, TestTimeout.Token());
        var blocker = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());
        await coordinator.LockManager.AcquireAsync(blocker.Sequence, LockResource.Object(TableId(database, "a")), LockMode.Exclusive, TestTimeout.Token());
        int openBefore = coordinator.GetOpenContexts().Count;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string sql = outcome == "fails" ? "SELECT a.id / 0, b.id FROM a JOIN b ON a.id = b.id" : Join;

        // Act
        var pending = session.ExecuteAsync(sql, cancellationToken: cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        int openWhileWaiting = coordinator.GetOpenContexts().Count;
        if (outcome == "canceled")
        {
            cancellation.Cancel();
        }
        else
        {
            await coordinator.RollbackAsync(blocker, CancellationToken.None);
        }

        Exception? failure = null;
        try
        {
            await ReadRowsAsync(await pending);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        int openAfter = coordinator.GetOpenContexts().Count;
        if (blocker.State == TransactionState.Active)
        {
            await coordinator.RollbackAsync(blocker, CancellationToken.None);
        }

        var rows = await RowsAsync(session, Join);
        var stateAfter = transaction.State;
        await transaction.CommitAsync(TestTimeout.Token());

        // Assert: one pin while a read-committed statement ran, none after it, whatever ended it;
        // a failed statement left the transaction usable.
        openWhileWaiting.ShouldBe(isolationLevel == IsolationLevel.ReadCommitted ? openBefore + 1 : openBefore);
        int blockerEnded = outcome == "canceled" ? 0 : 1;
        openAfter.ShouldBe(openBefore - blockerEnded);
        switch (outcome)
        {
            case "completes":
                failure.ShouldBeNull();
                break;
            case "fails":
                failure.ShouldBeAssignableTo<DatabaseException>().ShouldNotBeNull().Message.ShouldStartWith("COHSQLE001", Case.Sensitive);
                break;
            default:
                failure.ShouldBeAssignableTo<OperationCanceledException>();
                break;
        }

        stateAfter.ShouldBe(TransactionState.Active);
        rows.Count.ShouldBe(3);
    }

    /// <summary>
    /// The adversarial probe of #1363 as a bounded guard: read-committed scans, index seeks, index
    /// joins and nested-loop joins under writers that replace and delete rows and a purge loop.
    /// Before the fix a five-second run lost 861 rows in 521 statements. Set
    /// <c>COHESION_DATABASE_RC_PROBE_SECONDS</c> to run it longer.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Read committed: Statements under older writers and a purge loop never lose or repeat a row (#1363)")]
    public async Task Statements_UnderOlderWritersAndAPurgeLoop_ShouldNeverLoseOrRepeatARow()
    {
        // Arrange
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("COHESION_DATABASE_RC_PROBE_SECONDS"), out double configured) && configured > 0
            ? configured
            : 2;

        // Act
        var counts = await SqlReadCommittedProbe.RunAsync(TimeSpan.FromSeconds(seconds));
        string report = $"SQL read-committed probe, {seconds} s:{Environment.NewLine}{counts}";
        _output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("COHESION_DATABASE_RC_PROBE_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, report + Environment.NewLine);
        }

        // Assert: the purge did reclaim while the statements ran, and no statement lost a row.
        counts.Reclaimed.ShouldBeGreaterThan(0, report);
        counts.Anomalies.ShouldBe(0, report);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "rc-snapshot-pin" });

    private static async Task<SqlDatabase> CreateDatabaseAsync(SqlDatabaseEngine engine, bool indexed)
    {
        var database = await engine.CreateDatabaseAsync("db");
        await using var setup = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE a (id INT NOT NULL)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("CREATE TABLE b (id INT NOT NULL)", cancellationToken: TestTimeout.Token());
        if (indexed)
        {
            await setup.ExecuteAsync("CREATE INDEX ix_b_id ON b (id)", cancellationToken: TestTimeout.Token());
        }

        await setup.ExecuteAsync("INSERT INTO a (id) VALUES (1), (2), (3)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO b (id) VALUES (1), (2), (3)", cancellationToken: TestTimeout.Token());
        return database;
    }

    private static ulong TableId(SqlDatabase database, string name)
    {
        database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
        return table.ObjectId;
    }

    private static async Task<List<string>> RowsAsync(SqlDatabaseSession session, string sql)
        => await ReadRowsAsync(await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

    /// <summary>Reads each row as its values joined with commas.</summary>
    private static async Task<List<string>> ReadRowsAsync(QueryResult result)
    {
        await using var set = result.ShouldBeAssignableTo<QueryResultSet>().ShouldNotBeNull();
        var rows = new List<string>();
        await foreach (var row in set.GetRowsAsync(TestTimeout.Token()))
        {
            var values = new long[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = Convert.ToInt64(row.GetValue(index));
            }

            rows.Add(string.Join(",", values));
        }

        return rows;
    }
}
