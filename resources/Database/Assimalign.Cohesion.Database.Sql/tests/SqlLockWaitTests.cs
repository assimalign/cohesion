using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Lock waits under the SQL engine's referential locking (#1390). A cascading delete locks a parent
/// row before its child rows, and a child writer locks its child row before the parent row its
/// foreign key names, so the two can wait for each other; the lock manager's deadlock detector must
/// see every such cycle. It read its wait-for edges from a graph recorded when each request queued,
/// so a waiter whose holder changed while it waited (a release woke the request queued ahead of
/// it, or a compatible request was granted past it) kept an edge to a transaction that had left
/// and none to the one it now waited for. The #1370 review's stress runs saw two to five waits per
/// run pass 10 s; every one was such a cycle, or waited behind one.
/// </summary>
[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlLockWaitTests
{
    private readonly ITestOutputHelper _output;

    public SqlLockWaitTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The base's cycle, built step by step. The holder's update holds a child row; an updater that
    /// re-parents the child queues for it, then a deleter that already holds the new parent row.
    /// The holder rolls back, so the release grants the child row to the updater, queued first, and
    /// the deleter now waits for the updater. The updater's foreign-key check then waits for the
    /// parent row the deleter holds: the updater must be the deadlock victim at once. On the base
    /// the deleter's only edge named the holder, the cycle went unseen, and both waited until their
    /// statements' tokens expired.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lock waits: A foreign-key wait that closes a cycle through a writer queued behind the waiter is a deadlock, not a hang (#1390)")]
    public async Task ExecuteAsync_ForeignKeyWaitClosingACycleThroughALaterWaiter_ShouldFailAsADeadlock()
    {
        // Arrange: parents 1 and 2, and a child that references neither, so the holder's update
        // locks the child row and no parent row.
        string name = "lock-wait-cycle-" + Guid.NewGuid().ToString("N");
        await using var engine = SqlDatabaseEngine.Create("lock-wait-cycle", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync(name);
        await using var setup = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, v INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO p (id) VALUES (1), (2)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO c (id, pid, v) VALUES (10, NULL, 0)", cancellationToken: TestTimeout.Token());
        using var waits = new LockWaitListener(name);
        await using var holderSession = await database.CreateSessionAsync();
        await using var updaterSession = await database.CreateSessionAsync();
        await using var deleterSession = await database.CreateSessionAsync();
        var holder = await holderSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await holderSession.ExecuteAsync("UPDATE c SET v = 1 WHERE id = 10", cancellationToken: TestTimeout.Token());
        var updater = await updaterSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        var deleter = await deleterSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await deleterSession.ExecuteAsync("DELETE FROM p WHERE id = 2", cancellationToken: TestTimeout.Token());

        // Act: the updater queues for the child row, then the deleter; the holder's rollback hands
        // the row to the updater, whose foreign-key check then waits for the deleted parent row.
        var updating = updaterSession.ExecuteAsync("UPDATE c SET pid = 2 WHERE id = 10", cancellationToken: TestTimeout.Token()).AsTask();
        await waits.WaitForWaitAsync(updater.Context.Sequence, TestTimeout.Token());
        var deleting = deleterSession.ExecuteAsync("UPDATE c SET v = 2 WHERE id = 10", cancellationToken: TestTimeout.Token()).AsTask();
        await waits.WaitForWaitAsync(deleter.Context.Sequence, TestTimeout.Token());
        await holder.RollbackAsync(TestTimeout.Token());
        var deadlock = await Should.ThrowAsync<DatabaseTransactionDeadlockException>(async () => await updating);
        var updaterState = updater.State;
        await updater.RollbackAsync(TestTimeout.Token());
        var updated = await deleting;
        await deleter.CommitAsync(TestTimeout.Token());

        // Assert: the updater was the victim and its transaction survived the statement; the
        // deleter, granted the row once the updater rolled back, deleted the parent and updated the
        // child.
        deadlock.InnerException.ShouldBeOfType<TransactionDeadlockException>();
        deadlock.Message.ShouldContain($"Transaction {updater.Context.Sequence} was chosen as the deadlock victim requesting Shared", Case.Sensitive);
        updaterState.ShouldBe(TransactionState.Active);
        updated.AffectedCount.ShouldBe(1);
        (await RowsAsync(setup, "SELECT id FROM p")).ShouldBe(["1"]);
        (await RowsAsync(setup, "SELECT id, pid, v FROM c")).ShouldBe(["10,,2"]);
    }

    /// <summary>
    /// A snapshot cascade that waited for the lock of a child version whose writer then rolled
    /// back, which the stress probe below met, rarely, once deadlocks no longer stalled its workload. The
    /// cascades start from parent versions their snapshots hold but committed updates replaced, so
    /// they reach a child the writer is still inserting under the replacements (#1370). The first
    /// cascade locks the child and fails first-updater-wins, keeping the lock; the second waits for
    /// it, and meanwhile the writer rolls back and its undo deletes the version. The second must
    /// fail first-updater-wins as well: it read the slot with a read that throws for a slot that is
    /// gone, and on <c>19b1a495</c> failed with the storage's <c>StorageIOException</c> ("Page 7 is
    /// not allocated": the undo emptied the child table's only page and freed it). The probe met
    /// the same race once as <c>SlottedPageException</c>, for a deleted slot on a page still in use.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: A snapshot cascade whose child version was rolled back while it waited for the row fails first-updater-wins, not with a storage error (#1390)")]
    public async Task Cascade_SnapshotChildVersionRolledBackWhileTheCascadeWaited_ShouldFailFirstUpdaterWins()
    {
        // Arrange: a child table that cascades from two parents; each deleter's snapshot holds a
        // parent version a committed update then replaces.
        string name = "lock-wait-cascade-" + Guid.NewGuid().ToString("N");
        await using var engine = SqlDatabaseEngine.Create("lock-wait-cascade", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync(name);
        await using var setup = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY, v INT)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("CREATE TABLE q (id INT PRIMARY KEY, v INT)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, qid INT, " +
            "CONSTRAINT fk_cp FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE, " +
            "CONSTRAINT fk_cq FOREIGN KEY(qid) REFERENCES q(id) ON DELETE CASCADE)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO p (id, v) VALUES (1, 0)", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("INSERT INTO q (id, v) VALUES (1, 0)", cancellationToken: TestTimeout.Token());
        using var waits = new LockWaitListener(name);
        await using var deleterSession = await database.CreateSessionAsync();
        await using var firstSession = await database.CreateSessionAsync();
        await using var writerSession = await database.CreateSessionAsync();
        var deleter = await deleterSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        var first = await firstSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await setup.ExecuteAsync("UPDATE p SET v = 5 WHERE id = 1", cancellationToken: TestTimeout.Token());
        await setup.ExecuteAsync("UPDATE q SET v = 5 WHERE id = 1", cancellationToken: TestTimeout.Token());
        var writer = await writerSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await writerSession.ExecuteAsync("INSERT INTO c (id, pid, qid) VALUES (12, 1, 1)", cancellationToken: TestTimeout.Token());

        // Act: the first cascade locks the in-flight child and fails; the deleter's cascade waits
        // for that lock while the writer rolls back, then gets it when the first transaction ends.
        var firstConflict = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await firstSession.ExecuteAsync("DELETE FROM q WHERE id = 1", cancellationToken: TestTimeout.Token()));
        var deleting = deleterSession.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: TestTimeout.Token()).AsTask();
        await waits.WaitForWaitAsync(deleter.Context.Sequence, TestTimeout.Token());
        await writer.RollbackAsync(TestTimeout.Token());
        await first.RollbackAsync(TestTimeout.Token());
        var conflict = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await deleting);
        var stateAfter = deleter.State;
        await deleter.RollbackAsync(TestTimeout.Token());
        var retried = await setup.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: TestTimeout.Token());

        // Assert: both cascades failed first-updater-wins; the second names no writer, whose
        // version is gone; nothing was deleted until the retry.
        firstConflict.Message.ShouldContain("ON DELETE CASCADE reached a row version written by transaction", Case.Sensitive);
        conflict.InnerException.ShouldBeOfType<TransactionAbortedException>();
        conflict.Message.ShouldContain("'dbo.c'", Case.Sensitive);
        conflict.Message.ShouldContain("rolled back after the walk found it", Case.Sensitive);
        conflict.Message.ShouldContain("first-updater-wins", Case.Sensitive);
        stateAfter.ShouldBe(TransactionState.Active);
        retried.AffectedCount.ShouldBe(1);
        (await RowsAsync(setup, "SELECT id, v FROM q")).ShouldBe(["1,5"]);
        (await RowsAsync(setup, "SELECT id FROM c")).ShouldBeEmpty();
    }

    /// <summary>
    /// The stress probe (<see cref="SqlLockWaitProbe"/>): the #1370 review's workload, run until the
    /// lock manager has broken enough of its deadlocks, then drained. No wait may last 10 s; before
    /// the fix each 60-second run on the base saw 40 that did. Set
    /// <c>COHESION_DATABASE_LOCK_WAIT_PROBE_SECONDS</c> to run it longer,
    /// <c>COHESION_DATABASE_LOCK_WAIT_PROBE_SHAPE</c> to "parents,deleters,childWriters[,explicit]"
    /// for another workload ("1,1,14,1" concentrates it on one parent row in explicit transactions
    /// only, the starvation measurement in the Transactions DESIGN.md, "Grants respect the queue"), and
    /// <c>COHESION_DATABASE_LOCK_WAIT_PROBE_REPORT</c> to append its report to a file.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Lock waits: Cascading deletes, child writers and the purge never leave a lock wait 10 s long (#1390)")]
    public async Task Workload_CascadesChildWritersAndThePurge_ShouldNeverWaitTenSeconds()
    {
        // Arrange
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("COHESION_DATABASE_LOCK_WAIT_PROBE_SECONDS"), out double configured) && configured > 0
            ? configured
            : 3;
        int[] shape = Environment.GetEnvironmentVariable("COHESION_DATABASE_LOCK_WAIT_PROBE_SHAPE") is { Length: > 0 } configuredShape
            ? configuredShape.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray()
            : [SqlLockWaitProbe.DefaultParents, 3, 5];

        bool explicitOnly = shape.Length > 3 && shape[3] != 0;

        // Act
        var result = await SqlLockWaitProbe.RunAsync(TimeSpan.FromSeconds(seconds), shape[0], shape[1], shape[2], explicitOnly);
        string report = $"SQL lock-wait probe, {seconds} s, {shape[0]} parents, {shape[1]} deleters, {shape[2]} child writers" +
            $"{(explicitOnly ? ", explicit transactions only" : "")}:{Environment.NewLine}{result}";
        _output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("COHESION_DATABASE_LOCK_WAIT_PROBE_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, report + Environment.NewLine);
        }

        // Assert: the detector broke cycles while the workers committed, nothing failed but the
        // conflicts the workload provokes, and no wait reached the bound.
        result.Deadlocks.ShouldBeGreaterThanOrEqualTo(SqlLockWaitProbe.MinimumDeadlocks, report);
        result.Commits.ShouldBeGreaterThanOrEqualTo(SqlLockWaitProbe.MinimumCommits, report);
        result.Unexpected.ShouldBeEmpty(report);
        result.WaitsOf(SqlLockWaitProbe.LongWait).ShouldBeEmpty(report);
    }

    private static async Task<List<string>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var set = (await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token())).ShouldBeAssignableTo<DatabaseResultSet>().ShouldNotBeNull();
        var rows = new List<string>();
        await foreach (var row in set.GetRowsAsync(TestTimeout.Token()))
        {
            var values = new string[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = row.GetValue(index)?.ToString() ?? string.Empty;
            }

            rows.Add(string.Join(",", values));
        }

        return rows;
    }

}
