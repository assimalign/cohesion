using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// Persistent worker failures take a database offline (owner decision 25 of 2026-10-06): a
/// checkpoint, page write-back, write-ahead flush or version-purge worker that fails on one
/// database on as many passes in a row as its engine's limit allows makes the engine take that
/// database, and only that one, offline with the cause that names the worker. A transient failure
/// under the limit does not, a pass that finishes the database's work restarts the count, and a
/// database the engine no longer holds open is not counted. The engine takes the database offline
/// on a thread-pool thread, never the worker's, so a give-up that waits (on a journal lock a hung
/// fsync holds) never holds the worker's other databases; once it is offline, or once it closes,
/// the workers forget their failure records of it. The model engines' suites pin the same through
/// real storages.
/// </summary>
public sealed class DatabaseWorkerFailureLimitTests
{
    private const string EngineName = "test-engine";
    private const string WorkerName = EngineName + "/checkpoint";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a database failing the limit of passes in a row is taken offline through its engine, and only it")]
    public async Task ReportFailure_LimitOfPassesInARow_ShouldTakeOnlyThatDatabaseOffline()
    {
        // Arrange: database a fails every pass, b never does; the engine allows three failed passes.
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 3);
        await engine.CreateDatabaseAsync("a");
        await engine.CreateDatabaseAsync("b");
        var failure = new InvalidOperationException("checkpoint failed");
        int[] counts = new int[4];
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (self.Begin("a"))
            {
                counts[pass - 1] = self.Fail("a", failure, TimeSpan.Zero);
            }

            self.Begin("b").ShouldBeTrue();
        }, name: WorkerName);
        engine.Attach(worker);

        // Act
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        var afterTwo = engine.GiveUps;
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1 && worker.Fault is null);
        var afterThree = engine.GiveUps;
        worker.RunIteration(CancellationToken.None);

        // Assert: the third failed pass gives up on a; the engine then forgets the worker's record
        // (the fault clears with it), so the fourth failure counts from one and asks nothing.
        afterTwo.ShouldBeEmpty();
        taken.ShouldBeTrue();
        var giveUp = afterThree.ShouldHaveSingleItem();
        giveUp.Name.ShouldBe("a");
        giveUp.Cause.ShouldBe(StorageOfflineCause.CheckpointFailures);
        giveUp.Failure.ShouldBeSameAs(failure);
        giveUp.Taken.ShouldBeTrue();
        giveUp.Reason.ShouldContain($"'{WorkerName}'");
        giveUp.Reason.ShouldContain("database 'a'");
        giveUp.Reason.ShouldContain("3 passes in a row");
        counts.ShouldBe([1, 2, 3, 1]);
        engine.TakenOffline.ShouldBe(["a"]);
        engine.GiveUps.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a transient failure under the limit leaves the database online, and a finished pass restarts the count")]
    public async Task ReportFailure_TransientUnderTheLimit_ShouldNotGiveUp()
    {
        // Arrange: a fails on passes 1, 2, 4 and 5; pass 3 finishes its work.
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 3);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            if (pass != 3)
            {
                self.Fail("a", new InvalidOperationException($"pass {pass} failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act
        for (int pass = 1; pass <= 5; pass++)
        {
            worker.RunIteration(CancellationToken.None);
        }

        // Assert: never three in a row.
        engine.GiveUps.ShouldBeEmpty();
        engine.TakeOfflineCalls.ShouldBe(0);
        worker.ConsecutiveFailures.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: several failures of one database in a pass count as one failed pass")]
    public async Task ReportFailure_SeveralInOnePass_ShouldCountOnePass()
    {
        // Arrange: each pass fails a twice, as a write-back of a database with two file sets does.
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 2);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a").ShouldBeTrue();
            self.Fail("a", new InvalidOperationException("data file set"), TimeSpan.Zero);
            self.Fail("a", new InvalidOperationException("catalog file set"), TimeSpan.Zero);
        }, name: EngineName + "/page-writeback", kind: DatabaseEngineWorkerKind.PageWriteBack);
        engine.Attach(worker);

        // Act
        worker.RunIteration(CancellationToken.None);
        var afterOne = engine.GiveUps;
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // Assert: two passes, not four reports, reach the limit.
        afterOne.ShouldBeEmpty();
        taken.ShouldBeTrue();
        engine.TakenOffline.ShouldBe(["a"]);
        engine.GiveUps[0].Cause.ShouldBe(StorageOfflineCause.PageWriteBackFailures);
        engine.GiveUps[0].Reason.ShouldContain("2 passes in a row");
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Worker failure limit: each durability worker's failures take the database offline with its own cause; index maintenance never does")]
    [InlineData(DatabaseEngineWorkerKind.Checkpoint, StorageOfflineCause.CheckpointFailures)]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack, StorageOfflineCause.PageWriteBackFailures)]
    [InlineData(DatabaseEngineWorkerKind.WriteAheadFlush, StorageOfflineCause.WriteAheadFlushFailures)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge, StorageOfflineCause.VersionPurgeFailures)]
    [InlineData(DatabaseEngineWorkerKind.IndexMaintenance, null)]
    public async Task ReportFailure_EachKind_ShouldGiveUpWithItsCause(DatabaseEngineWorkerKind kind, StorageOfflineCause? cause)
    {
        // Arrange: the engine gives up after one failed pass.
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 1);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a").ShouldBeTrue();
            self.Fail("a", new InvalidOperationException("failed"), TimeSpan.Zero);
        }, name: EngineName + "/" + kind, kind: kind);
        engine.Attach(worker);

        // Act
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1, cause is null ? TimeSpan.FromMilliseconds(200) : Timeout);

        // Assert
        if (cause is { } expected)
        {
            taken.ShouldBeTrue();
            engine.GiveUps[0].Cause.ShouldBe(expected);
            engine.TakenOffline.ShouldBe(["a"]);
        }
        else
        {
            taken.ShouldBeFalse();
            engine.GiveUps.ShouldBeEmpty();
            worker.ConsecutiveFailures.ShouldBe(2);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a worker no engine owns gives up on nothing")]
    public void ReportFailure_FreeWorker_ShouldGiveUpOnNothing()
    {
        // Arrange: never attached.
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a");
            self.Fail("a", new InvalidOperationException("failed"), TimeSpan.Zero);
        });

        // Act
        for (int pass = 0; pass < DatabaseEngine.DefaultWorkerFailureLimit * 2; pass++)
        {
            worker.RunIteration(CancellationToken.None);
        }

        bool queued = worker.GiveUp("a", StorageOfflineCause.CheckpointFailures, "the test gives up", new InvalidOperationException("failed"));

        // Assert
        worker.ConsecutiveFailures.ShouldBe(DatabaseEngine.DefaultWorkerFailureLimit * 2);
        queued.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a database the engine does not hold open is not taken offline, and the worker goes on")]
    public async Task ReportFailure_DatabaseNotOpen_ShouldBeRefusedByTheLeaf()
    {
        // Arrange: the worker fails on a name the engine has no open database for.
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 1);
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("missing");
            self.Fail("missing", new InvalidOperationException("failed"), TimeSpan.Zero);
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: a failed pass asks once no earlier ask of the database is still running.
        var results = new List<bool> { worker.RunIteration(CancellationToken.None) };
        bool askedOnce = await Eventually(() => engine.GiveUps.Count == 1);
        bool askedAgain = await Eventually(() =>
        {
            results.Add(worker.RunIteration(CancellationToken.None));
            return engine.GiveUps.Count >= 2;
        });

        // Assert: asked again, refused each time; the failed passes are the worker's own, and the
        // record the leaf's refusal left keeps counting.
        results.ShouldAllBe(succeeded => !succeeded);
        askedOnce.ShouldBeTrue();
        askedAgain.ShouldBeTrue();
        engine.GiveUps.ShouldAllBe(giveUp => !giveUp.Taken);
        engine.TakenOffline.ShouldBeEmpty();
        worker.ConsecutiveFailures.ShouldBe(results.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: an engine whose disposal started gives up on nothing")]
    public async Task ReportFailure_EngineDisposed_ShouldGiveUpOnNothing()
    {
        // Arrange
        var engine = new TestEngine(EngineName, workerFailureLimit: 1);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a");
            self.Fail("a", new InvalidOperationException("failed"), TimeSpan.Zero);
        }, name: WorkerName);
        engine.Attach(worker);
        await engine.DisposeAsync();

        // Act
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(100);

        // Assert
        engine.GiveUps.ShouldBeEmpty();
        engine.TakeOfflineCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a worker gives up on a database with an engine's cause only, through its engine")]
    public async Task TakeDatabaseOffline_ShouldTakeOnlyAnEngineCause()
    {
        // Arrange
        await using var engine = new TestEngine(EngineName);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((_, _) => { }, name: WorkerName);
        engine.Attach(worker);
        var failure = new InvalidOperationException("checkpoint failed");

        // Act
        var deviceCause = Should.Throw<ArgumentOutOfRangeException>(() => worker.GiveUp("a", StorageOfflineCause.HeaderWrite, "a device failure", failure));
        Should.Throw<ArgumentException>(() => worker.GiveUp("a", StorageOfflineCause.JournalSizeLimit, " ", failure));
        Should.Throw<ArgumentNullException>(() => worker.GiveUp("a", StorageOfflineCause.JournalSizeLimit, "the cap", null!));
        bool queued = worker.GiveUp("a", StorageOfflineCause.JournalSizeLimit, "its journal passed the cap", failure);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // A second give-up is queued once the first ended (until then the call queues nothing).
        bool again = await Eventually(() => worker.GiveUp("a", StorageOfflineCause.JournalSizeLimit, "its journal passed the cap", failure));
        bool refused = await Eventually(() => engine.GiveUps.Count == 2);

        // Assert: the leaf takes the database offline once and refuses it after.
        deviceCause.ParamName.ShouldBe("cause");
        queued.ShouldBeTrue();
        taken.ShouldBeTrue();
        again.ShouldBeTrue();
        refused.ShouldBeTrue();
        var giveUp = engine.GiveUps[0];
        giveUp.Cause.ShouldBe(StorageOfflineCause.JournalSizeLimit);
        giveUp.Reason.ShouldBe("its journal passed the cap");
        giveUp.Failure.ShouldBeSameAs(failure);
        giveUp.Taken.ShouldBeTrue();
        engine.GiveUps[1].Taken.ShouldBeFalse();
    }

    /// <summary>
    /// The give-up never runs on the worker's thread (owner decision 25 review): taking a storage
    /// offline latches its journal under the journal's lock, which a durable flush holds through a
    /// hung fsync. Here the leaf's core blocks for database a; the worker's next pass still begins
    /// database b and returns, a failure of a reported meanwhile queues no second give-up, and
    /// releasing the core takes a offline once.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a give-up that blocks in the leaf never holds the worker's next pass, and runs once per database")]
    public async Task GiveUp_LeafBlocks_ShouldNotHoldTheWorkersOtherDatabases()
    {
        // Arrange: a fails every pass and is retried at once; the leaf's core blocks until released.
        using var gate = new ManualResetEventSlim(false);
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 1) { TakeOfflineGate = gate };
        await engine.CreateDatabaseAsync("a");
        await engine.CreateDatabaseAsync("b");
        int bVisits = 0;
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }

            if (self.Begin("b"))
            {
                Interlocked.Increment(ref bVisits);
            }
        }, name: WorkerName);
        engine.Attach(worker);
        int workerThread = -1;

        // Act: both passes run on one thread, as the worker's pump runs them.
        var passes = Task.Run(() =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            worker.RunIteration(CancellationToken.None);
            worker.RunIteration(CancellationToken.None);
        });
        bool returned = await Task.WhenAny(passes, Task.Delay(TimeSpan.FromSeconds(10))) == passes;
        bool entered = await Eventually(() => engine.TakeOfflineCalls == 1);
        var takenWhileBlocked = engine.TakenOffline;

        gate.Set();
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);
        await passes.WaitAsync(Timeout);

        // Assert: the passes returned while the core was blocked, b was visited by both, the
        // second pass's failure of a queued nothing, and a went offline once, on another thread.
        returned.ShouldBeTrue();
        entered.ShouldBeTrue();
        takenWhileBlocked.ShouldBeEmpty();
        Volatile.Read(ref bVisits).ShouldBe(2);
        taken.ShouldBeTrue();
        engine.TakeOfflineCalls.ShouldBe(1);
        engine.GiveUps.ShouldHaveSingleItem().Taken.ShouldBeTrue();
        lock (engine.TakeOfflineThreads)
        {
            engine.TakeOfflineThreads.ShouldNotContain(workerThread);
        }
    }

    /// <summary>
    /// Once the engine took a database offline it forgets the worker's failure record of it (owner
    /// decision 25 review): the engine reports Running and the worker holds no fault with no further
    /// pass, and a database whose failure persists is counted from one again, so a reopened
    /// database is not taken offline on its first failure.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: once a database is offline the worker's record of it ends, and a later failure counts from one")]
    public async Task GiveUp_Taken_ShouldEndTheWorkersRecord()
    {
        // Arrange
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 3);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: three failed passes take a offline; then, with no pass in between, the state.
        for (int pass = 0; pass < 3; pass++)
        {
            worker.RunIteration(CancellationToken.None);
        }

        bool taken = await Eventually(() => engine.TakenOffline.Count == 1 && worker.Fault is null);
        var state = engine.State;
        int consecutive = worker.ConsecutiveFailures;

        // The database fails twice more (the double keeps it online): under the limit of three.
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(100);

        // Assert
        taken.ShouldBeTrue();
        state.ShouldBe(EngineState.Running);
        consecutive.ShouldBe(0);
        worker.ConsecutiveFailures.ShouldBe(2);
        engine.GiveUps.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A database's close ends every worker's failure record of it (owner decision 25 review): the
    /// failures belong to the instance that closed, so the engine no longer reports Faulted for it,
    /// and the database opened again counts its failures from one.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a database's close ends the worker's record of it, and the reopened database counts from one")]
    public async Task ForgetClosedDatabase_ShouldEndTheWorkersRecord()
    {
        // Arrange: a fails every pass; the engine allows three.
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 3);
        var database = await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: two failed passes, a holder closes the database, and it is opened again.
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        var faulted = engine.State;
        await database.DisposeAsync();
        var afterClose = engine.State;
        var fault = worker.Fault;
        await engine.OpenDatabaseAsync("a");
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(100);

        // Assert: four failures, never three in a row on one instance.
        faulted.ShouldBe(EngineState.Faulted);
        afterClose.ShouldBe(EngineState.Running);
        fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(2);
        engine.GiveUps.ShouldBeEmpty();
    }

    /// <summary>
    /// A give-up the leaf's core fails is the worker's own failure (owner decision 25 review): it is
    /// held in the worker's fault, the engine reports Faulted, nothing escapes the thread-pool
    /// thread, and the database's next failure asks again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a give-up the leaf fails is recorded on the worker, and the next failure asks again")]
    public async Task GiveUp_LeafThrows_ShouldRecordTheFailureOnTheWorker()
    {
        // Arrange
        var leafFailure = new InvalidOperationException("the leaf could not take the database offline");
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 1) { TakeOfflineFailure = leafFailure };
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: the first give-up fails in the leaf; the leaf then recovers, and a later failed pass
        // asks again once the failed give-up ended.
        worker.RunIteration(CancellationToken.None);
        bool recorded = await Eventually(() => ReferenceEquals(worker.Fault, leafFailure));
        var state = engine.State;
        engine.TakeOfflineFailure = null;
        var watch = Stopwatch.StartNew();
        while (engine.TakenOffline.Count == 0 && watch.Elapsed < Timeout)
        {
            worker.RunIteration(CancellationToken.None);
            await Task.Delay(10);
        }

        // Assert
        recorded.ShouldBeTrue();
        state.ShouldBe(EngineState.Faulted);
        engine.TakenOffline.ShouldBe(["a"]);
        engine.TakeOfflineCalls.ShouldBeGreaterThanOrEqualTo(2);
        engine.GiveUps.ShouldHaveSingleItem().Taken.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the worker failure limit is the constructor's, ten by default, and at least one")]
    public async Task Constructor_WorkerFailureLimit_ShouldBeTheConstructorsAndPositive()
    {
        // Act
        await using var defaulted = new TestEngine(EngineName);
        await using var five = new TestEngine(EngineName, workerFailureLimit: 5);
        var zero = Should.Throw<ArgumentOutOfRangeException>(() => new TestEngine(EngineName, workerFailureLimit: 0));

        // Assert
        DatabaseEngine.DefaultWorkerFailureLimit.ShouldBe(10);
        defaulted.WorkerFailureLimit.ShouldBe(10);
        five.WorkerFailureLimit.ShouldBe(5);
        zero.ParamName.ShouldBe("workerFailureLimit");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the offline error lookup checks the name and the engine before the leaf's core")]
    public async Task GetOfflineError_ShouldCheckThenForwardToTheCore()
    {
        // Arrange
        var engine = new TestEngine(EngineName);

        // Act
        var online = engine.GetOfflineError("a");
        var empty = Should.Throw<ArgumentException>(() => engine.GetOfflineError(default));
        await engine.DisposeAsync();
        Should.Throw<ObjectDisposedException>(() => engine.GetOfflineError("a"));

        // Assert
        online.ShouldBeNull();
        empty.ParamName.ShouldBe("name");
        engine.OfflineErrorLookups.ShouldBe(["a"]);
    }

    // Polls a condition the engine's queued give-up makes true, for at most the timeout.
    private static async Task<bool> Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > (timeout ?? Timeout))
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }
}
