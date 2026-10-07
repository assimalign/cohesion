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
/// Persistent worker failures take a database offline (owner decisions 25 of 2026-10-06 and 42 of
/// 2026-10-07): a checkpoint, page write-back, write-ahead flush or version-purge worker whose
/// failures of one database last the engine's window across at least its minimum of failed passes
/// in a row makes the engine take that database, and only that one, offline with the cause that
/// names the worker. A failure under the window, or under the minimum of passes, does not; a pass
/// that finishes the database's work ends the streak, and the window starts again at the next
/// failure; and a database the engine no longer holds open is not counted. The window is measured
/// on the engine's clock, a <see cref="ManualTimeProvider"/> here, so no test waits for it. The
/// engine takes the database offline on a thread-pool thread, never the worker's, so a give-up that
/// waits (on a journal lock a hung fsync holds) never holds the worker's other databases; once it
/// is offline, or once it closes, the workers forget their failure records of it. The engine tells
/// a database's failure from its own (<see cref="DatabaseEngine.HasFailingWorker"/>,
/// <see cref="DatabaseEngine.HasEngineWideFailure"/>). The model engines' suites pin the same
/// through real storages.
/// </summary>
public sealed class DatabaseWorkerFailureWindowTests
{
    private const string EngineName = "test-engine";
    private const string WorkerName = EngineName + "/checkpoint";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(100);

    [Theory(DisplayName = "Cohesion Test [Database] - Worker failure window: a failure lasting the window across the minimum of passes takes only that database offline, with the worker's cause")]
    [InlineData(DatabaseEngineWorkerKind.Checkpoint, StorageOfflineCause.CheckpointFailures)]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack, StorageOfflineCause.PageWriteBackFailures)]
    [InlineData(DatabaseEngineWorkerKind.WriteAheadFlush, StorageOfflineCause.WriteAheadFlushFailures)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge, StorageOfflineCause.VersionPurgeFailures)]
    [InlineData(DatabaseEngineWorkerKind.IndexMaintenance, null)]
    public async Task ReportFailure_PastTheWindowAcrossTheMinimumPasses_ShouldTakeOnlyThatDatabaseOffline(DatabaseEngineWorkerKind kind, StorageOfflineCause? cause)
    {
        // Arrange: a fails every pass, b never does; a window of 100 s and a minimum of three
        // failed passes, on a clock the test moves.
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: 3, time: clock);
        await engine.CreateDatabaseAsync("a");
        await engine.CreateDatabaseAsync("b");
        var failure = new InvalidOperationException("the worker's work failed");
        var counts = new List<int>();
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                counts.Add(self.Fail("a", failure, TimeSpan.Zero));
            }

            self.Begin("b").ShouldBeTrue();
        }, name: EngineName + "/" + kind, kind: kind);
        engine.Attach(worker);

        // Act: three failed passes 50 s apart, the third 100 s after the first.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(Window / 2);
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(50);
        var beforeTheWindow = engine.GiveUps;
        clock.Advance(Window / 2);
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1, cause is null ? TimeSpan.FromMilliseconds(200) : Timeout);

        // Assert: b is never touched; a goes offline on the third failed pass.
        beforeTheWindow.ShouldBeEmpty();
        counts.ShouldBe([1, 2, 3]);
        if (cause is { } expected)
        {
            taken.ShouldBeTrue();
            var giveUp = engine.GiveUps.ShouldHaveSingleItem();
            giveUp.Name.ShouldBe("a");
            giveUp.Cause.ShouldBe(expected);
            giveUp.Failure.ShouldBeSameAs(failure);
            giveUp.Taken.ShouldBeTrue();
            giveUp.Reason.ShouldContain($"'{worker.Name}'");
            giveUp.Reason.ShouldContain("database 'a'");
            giveUp.Reason.ShouldContain("3 passes in a row over 100 s", Case.Sensitive);
            giveUp.Reason.ShouldContain("at least the engine's window of 100 s", Case.Sensitive);
            engine.TakenOffline.ShouldBe(["a"]);
        }
        else
        {
            taken.ShouldBeFalse();
            engine.GiveUps.ShouldBeEmpty();
            worker.ConsecutiveFailures.ShouldBe(3);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Worker failure window: a failure under the window leaves the database online, however many passes it spans")]
    [InlineData(DatabaseEngineWorkerKind.Checkpoint)]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack)]
    [InlineData(DatabaseEngineWorkerKind.WriteAheadFlush)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge)]
    public async Task ReportFailure_UnderTheWindow_ShouldLeaveTheDatabaseOnline(DatabaseEngineWorkerKind kind)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: 3, time: clock);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException($"pass {pass} failed"), TimeSpan.Zero);
            }
        }, name: EngineName + "/" + kind, kind: kind);
        engine.Attach(worker);

        // Act: fifty failed passes two seconds apart, the last 98 s after the first.
        for (int pass = 0; pass < 50; pass++)
        {
            if (pass > 0)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
            }

            worker.RunIteration(CancellationToken.None);
        }

        await Task.Delay(100);
        var underTheWindow = engine.GiveUps;
        int consecutive = worker.ConsecutiveFailures;

        // The fifty-first, 100 s after the first, reaches the window.
        clock.Advance(TimeSpan.FromSeconds(2));
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // Assert
        underTheWindow.ShouldBeEmpty();
        consecutive.ShouldBe(50);
        taken.ShouldBeTrue();
        engine.GiveUps.ShouldHaveSingleItem().Reason.ShouldContain("51 passes in a row over 100 s", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Worker failure window: a failure past the window but under the minimum of passes leaves the database online")]
    [InlineData(DatabaseEngineWorkerKind.Checkpoint)]
    [InlineData(DatabaseEngineWorkerKind.PageWriteBack)]
    [InlineData(DatabaseEngineWorkerKind.WriteAheadFlush)]
    [InlineData(DatabaseEngineWorkerKind.VersionPurge)]
    public async Task ReportFailure_PastTheWindowUnderTheMinimumPasses_ShouldLeaveTheDatabaseOnline(DatabaseEngineWorkerKind kind)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: 3, time: clock);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException($"pass {pass} failed"), TimeSpan.Zero);
            }
        }, name: EngineName + "/" + kind, kind: kind);
        engine.Attach(worker);

        // Act: two failed passes 200 s apart: twice the window, one pass short of the minimum.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(Window * 2);
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(100);
        var underTheMinimum = engine.GiveUps;

        // The third reaches the minimum.
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // Assert
        underTheMinimum.ShouldBeEmpty();
        taken.ShouldBeTrue();
        engine.GiveUps.ShouldHaveSingleItem().Reason.ShouldContain("3 passes in a row over 200 s", Case.Sensitive);
    }

    /// <summary>
    /// The window is time, so every worker's pace gives up about the window after its first failure
    /// (owner decision 42 of 2026-10-07), at the defaults: a checkpoint retried once a second on its
    /// 101st failed pass, 100 s after the first; a version purge's full pass, once per 60 s
    /// maintenance interval, on its third, at 120 s; and a rolled-back writer's deferred undo,
    /// retried at 100 ms doubling up to that interval (the coordinator's schedule, #1226; the
    /// worker reports it with no backoff of its own), on its tenth, at 102.2 s. At decision 35's
    /// hundred failed passes the purge took about 100 minutes and the deferred undo about 92. Each
    /// pass reports with no backoff of the worker's, and the clock moves by the pace between passes.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database] - Worker failure window: every worker's pace gives up about the window after its first failure")]
    [InlineData("checkpoint", 101, "100")]
    [InlineData("version-purge-full-pass", 3, "120")]
    [InlineData("deferred-undo", 10, "102.2")]
    public async Task ReportFailure_EachWorkersPace_ShouldGiveUpAboutTheWindowAfterTheFirstFailure(string pace, int passes, string seconds)
    {
        // Arrange: the defaults, a window of 100 s and a minimum of three passes.
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, time: clock);
        await engine.CreateDatabaseAsync("a");
        var kind = pace == "checkpoint" ? DatabaseEngineWorkerKind.Checkpoint : DatabaseEngineWorkerKind.VersionPurge;
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException($"pass {pass} failed"), TimeSpan.Zero);
            }
        }, name: EngineName + "/" + pace, kind: kind);
        engine.Attach(worker);
        var delay = pace switch
        {
            "checkpoint" => DatabaseEngineWorker.FailureBackoff,
            "version-purge-full-pass" => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromMilliseconds(100),
        };

        // Act: the pace's failed passes until half a window past the window; a second streak
        // cannot reach the window in that time.
        worker.RunIteration(CancellationToken.None);
        while (clock.Elapsed < Window * 1.5)
        {
            if (pace == "deferred-undo")
            {
                // Each failed retry doubles the delay, up to the maintenance interval.
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
            }

            clock.Advance(delay);
            if (clock.Elapsed < Window * 1.5)
            {
                worker.RunIteration(CancellationToken.None);
            }
        }

        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // Assert
        DatabaseEngine.DefaultWorkerFailureWindow.ShouldBe(Window);
        taken.ShouldBeTrue();
        engine.GiveUps.ShouldHaveSingleItem().Reason.ShouldContain($"{passes} passes in a row over {seconds} s", Case.Sensitive);
    }

    /// <summary>
    /// The deferred-undo path (#1226): a rolled-back writer's undo that keeps failing is retried on
    /// its coordinator's schedule, 100 ms doubling up to the 60 s maintenance interval, and the
    /// version-purge worker reports each failed retry with no backoff of its own. Its failures take
    /// only that database offline, on the first retry that is past the window and reaches the
    /// minimum of passes: the tenth, at 102.2 s, at the default minimum of three; with a minimum of
    /// twelve, the tenth and eleventh retries, past the window, leave it online, and the twelfth,
    /// at 222.2 s, takes it offline. Every retry before the window leaves it online.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database] - Worker failure window: a deferred undo that keeps failing takes only its database offline past the window across the minimum of passes")]
    [InlineData(3, 10, "102.2")]
    [InlineData(12, 12, "222.2")]
    public async Task ReportFailure_DeferredUndoKeepsFailing_ShouldTakeOnlyItsDatabaseOfflinePastTheWindow(int minimumPasses, int givingUpPass, string seconds)
    {
        // Arrange: the version-purge worker retries a's deferred undo, which keeps failing; b's
        // purge succeeds.
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: minimumPasses, time: clock);
        await engine.CreateDatabaseAsync("a");
        await engine.CreateDatabaseAsync("b");
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException($"the deferred undo failed on retry {pass}"), TimeSpan.Zero);
            }

            self.Begin("b").ShouldBeTrue();
        }, name: EngineName + "/version-purge", kind: DatabaseEngineWorkerKind.VersionPurge);
        engine.Attach(worker);

        // Act: the retries before the one that gives up, on the coordinator's schedule.
        var delay = TimeSpan.FromMilliseconds(100);
        var afterEachRetry = new List<int>();
        for (int retry = 1; retry < givingUpPass; retry++)
        {
            worker.RunIteration(CancellationToken.None);
            afterEachRetry.Add(engine.GiveUps.Count);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
            clock.Advance(delay);
        }

        await Task.Delay(100);
        var beforeTheGivingUpRetry = engine.GiveUps;
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // Assert
        afterEachRetry.ShouldAllBe(count => count == 0);
        beforeTheGivingUpRetry.ShouldBeEmpty();
        taken.ShouldBeTrue();
        var giveUp = engine.GiveUps.ShouldHaveSingleItem();
        giveUp.Name.ShouldBe("a");
        giveUp.Cause.ShouldBe(StorageOfflineCause.VersionPurgeFailures);
        giveUp.Reason.ShouldContain($"{givingUpPass} passes in a row over {seconds} s", Case.Sensitive);
        engine.TakenOffline.ShouldBe(["a"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: a finished pass ends the streak, and the window starts again at the next failure")]
    public async Task ReportFailure_FinishedPassBetweenFailures_ShouldStartTheWindowAgain()
    {
        // Arrange: a fails on every pass but the fourth.
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: 3, time: clock);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            if (pass != 4)
            {
                self.Fail("a", new InvalidOperationException($"pass {pass} failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: passes at 0, 45 and 90 s fail; the one at 95 s finishes a's work; the ones at 110,
        // 150, 190 and 205 s fail: 205 s after the first failure, 95 s into the new streak.
        int[] times = [0, 45, 90, 95, 110, 150, 190, 205];
        foreach (int time in times)
        {
            clock.Advance(TimeSpan.FromSeconds(time) - clock.Elapsed);
            worker.RunIteration(CancellationToken.None);
        }

        await Task.Delay(100);
        var underTheWindow = engine.GiveUps;

        // At 210 s the new streak has lasted the window.
        clock.Advance(TimeSpan.FromSeconds(5));
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // Assert
        underTheWindow.ShouldBeEmpty();
        taken.ShouldBeTrue();
        engine.GiveUps.ShouldHaveSingleItem().Reason.ShouldContain("5 passes in a row over 100 s", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: several failures of one database in a pass count as one failed pass")]
    public async Task ReportFailure_SeveralInOnePass_ShouldCountOnePass()
    {
        // Arrange: each pass fails a twice, as a write-back of a database with two file sets does;
        // the minimum is two passes.
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: 2, time: clock);
        await engine.CreateDatabaseAsync("a");
        var counts = new List<int>();
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a").ShouldBeTrue();
            counts.Add(self.Fail("a", new InvalidOperationException("data file set"), TimeSpan.Zero));
            counts.Add(self.Fail("a", new InvalidOperationException("catalog file set"), TimeSpan.Zero));
        }, name: EngineName + "/page-writeback", kind: DatabaseEngineWorkerKind.PageWriteBack);
        engine.Attach(worker);

        // Act: one pass, the window, a second pass.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(Window);
        await Task.Delay(50);
        var afterOne = engine.GiveUps;
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

        // Assert: two passes, not four reports, reach the minimum. (The second pass's second report
        // may already find the record the give-up ended, so it is not asserted.)
        afterOne.ShouldBeEmpty();
        counts.GetRange(0, 3).ShouldBe([1, 1, 2]);
        taken.ShouldBeTrue();
        engine.TakenOffline.ShouldBe(["a"]);
        engine.GiveUps[0].Cause.ShouldBe(StorageOfflineCause.PageWriteBackFailures);
        engine.GiveUps[0].Reason.ShouldContain("2 passes in a row", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: a worker no engine owns gives up on nothing")]
    public void ReportFailure_FreeWorker_ShouldGiveUpOnNothing()
    {
        // Arrange: never attached.
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a");
            self.Fail("a", new InvalidOperationException("failed"), TimeSpan.Zero);
        });

        // Act
        for (int pass = 0; pass < 100; pass++)
        {
            worker.RunIteration(CancellationToken.None);
        }

        bool queued = worker.GiveUp("a", StorageOfflineCause.CheckpointFailures, "the test gives up", new InvalidOperationException("failed"));

        // Assert
        worker.ConsecutiveFailures.ShouldBe(100);
        queued.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: a database the engine does not hold open is not taken offline, and the worker goes on")]
    public async Task ReportFailure_DatabaseNotOpen_ShouldBeRefusedByTheLeaf()
    {
        // Arrange: the worker fails on a name the engine has no open database for; a window of a
        // second and a minimum of one pass.
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: TimeSpan.FromSeconds(1), workerFailureMinimumPasses: 1, time: clock);
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("missing");
            self.Fail("missing", new InvalidOperationException("failed"), TimeSpan.Zero);
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: past the window, a failed pass asks once no earlier ask of the database is still
        // running.
        var results = new List<bool> { worker.RunIteration(CancellationToken.None) };
        clock.Advance(TimeSpan.FromSeconds(1));
        results.Add(worker.RunIteration(CancellationToken.None));
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

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: an engine whose disposal started gives up on nothing")]
    public async Task ReportFailure_EngineDisposed_ShouldGiveUpOnNothing()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var engine = new TestEngine(EngineName, workerFailureWindow: TimeSpan.FromSeconds(1), workerFailureMinimumPasses: 1, time: clock);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a");
            self.Fail("a", new InvalidOperationException("failed"), TimeSpan.Zero);
        }, name: WorkerName);
        engine.Attach(worker);
        await engine.DisposeAsync();

        // Act: two failed passes a window apart.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(100);

        // Assert
        engine.GiveUps.ShouldBeEmpty();
        engine.TakeOfflineCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: a worker gives up on a database with an engine's cause only, through its engine")]
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
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: a give-up that blocks in the leaf never holds the worker's next pass, and runs once per database")]
    public async Task GiveUp_LeafBlocks_ShouldNotHoldTheWorkersOtherDatabases()
    {
        // Arrange: a fails every pass and is retried at once; the leaf's core blocks until released.
        using var gate = new ManualResetEventSlim(false);
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: TimeSpan.FromSeconds(1), workerFailureMinimumPasses: 1, time: clock)
        {
            TakeOfflineGate = gate,
        };
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

        // The streak starts a window before the passes under test.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        int workerThread = -1;
        Exception? passFailure = null;
        using var passesDone = new ManualResetEventSlim(false);
        using var releasePump = new ManualResetEventSlim(false);

        // Act: both passes run on one dedicated thread, as the worker's pump runs them. The thread
        // stays alive until the assertions ran: the runtime reuses a managed thread id once its
        // thread ends, and a pool thread that ran the passes would pick up the queued give-up next.
        var pump = new Thread(() =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            try
            {
                worker.RunIteration(CancellationToken.None);
                worker.RunIteration(CancellationToken.None);
            }
            catch (Exception exception)
            {
                // Any failure of a pass is handed to the test thread, which asserts there was none.
                passFailure = exception;
            }

            passesDone.Set();
            releasePump.Wait();
        })
        { IsBackground = true, Name = "test worker pump" };
        pump.Start();

        try
        {
            bool returned = passesDone.Wait(TimeSpan.FromSeconds(10));
            bool entered = await Eventually(() => engine.TakeOfflineCalls == 1);
            var takenWhileBlocked = engine.TakenOffline;

            gate.Set();
            bool taken = await Eventually(() => engine.TakenOffline.Count == 1);

            // Assert: the passes returned while the core was blocked, b was visited by every pass,
            // the second pass's failure of a queued nothing, and a went offline once, on another
            // thread.
            returned.ShouldBeTrue();
            passFailure.ShouldBeNull();
            entered.ShouldBeTrue();
            takenWhileBlocked.ShouldBeEmpty();
            Volatile.Read(ref bVisits).ShouldBe(3);
            taken.ShouldBeTrue();
            engine.TakeOfflineCalls.ShouldBe(1);
            engine.GiveUps.ShouldHaveSingleItem().Taken.ShouldBeTrue();
            lock (engine.TakeOfflineThreads)
            {
                engine.TakeOfflineThreads.ShouldNotContain(workerThread);
            }
        }
        finally
        {
            gate.Set();
            releasePump.Set();
            pump.Join(Timeout);
        }
    }

    /// <summary>
    /// Once the engine took a database offline it forgets the worker's failure record of it (owner
    /// decision 25 review): the engine reports Running, the worker holds no fault and the engine no
    /// failing worker for the database, with no further pass, and a database whose failure
    /// persists starts a new streak, so a reopened database is not taken offline on its first
    /// failure.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: once a database is offline the worker's record of it ends, and a later failure starts a new streak")]
    public async Task GiveUp_Taken_ShouldEndTheWorkersRecord()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: 3, time: clock);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: three failed passes over the window take a offline; then, with no pass in between,
        // the state.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(Window);
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        bool taken = await Eventually(() => engine.TakenOffline.Count == 1 && worker.Fault is null);
        var state = engine.State;
        bool failing = engine.HasFailingWorker("a");
        int consecutive = worker.ConsecutiveFailures;

        // The database fails twice more (the double keeps it online), a window later: a new streak
        // of two passes.
        clock.Advance(Window);
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(100);

        // Assert
        taken.ShouldBeTrue();
        state.ShouldBe(EngineState.Running);
        failing.ShouldBeFalse();
        consecutive.ShouldBe(0);
        worker.ConsecutiveFailures.ShouldBe(2);
        engine.GiveUps.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A database's close ends every worker's failure record of it (owner decision 25 review): the
    /// failures belong to the instance that closed, so the engine no longer reports Faulted for it,
    /// and the database opened again starts a new streak.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: a database's close ends the worker's record of it, and the reopened database starts a new streak")]
    public async Task ForgetClosedDatabase_ShouldEndTheWorkersRecord()
    {
        // Arrange: a fails every pass.
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: Window, workerFailureMinimumPasses: 3, time: clock);
        var database = await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: two failed passes a window apart, a holder closes the database, and it is opened
        // again and fails twice more.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(Window);
        worker.RunIteration(CancellationToken.None);
        var faulted = engine.State;
        bool failing = engine.HasFailingWorker("a");
        await database.DisposeAsync();
        var afterClose = engine.State;
        bool failingAfterClose = engine.HasFailingWorker("a");
        var fault = worker.Fault;
        await engine.OpenDatabaseAsync("a");
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        await Task.Delay(100);

        // Assert: four failures over the window, never three in a row on one instance.
        faulted.ShouldBe(EngineState.Faulted);
        failing.ShouldBeTrue();
        afterClose.ShouldBe(EngineState.Running);
        failingAfterClose.ShouldBeFalse();
        fault.ShouldBeNull();
        worker.ConsecutiveFailures.ShouldBe(2);
        engine.GiveUps.ShouldBeEmpty();
    }

    /// <summary>
    /// A give-up the leaf's core fails is the worker's own failure (owner decision 25 review): it is
    /// held in the worker's fault, the engine reports Faulted and a failure of its own as a whole
    /// (the database's record stays too), nothing escapes the thread-pool thread, and the
    /// database's next failure asks again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure window: a give-up the leaf fails is recorded on the worker, and the next failure asks again")]
    public async Task GiveUp_LeafThrows_ShouldRecordTheFailureOnTheWorker()
    {
        // Arrange
        var leafFailure = new InvalidOperationException("the leaf could not take the database offline");
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(EngineName, workerFailureWindow: TimeSpan.FromSeconds(1), workerFailureMinimumPasses: 1, time: clock)
        {
            TakeOfflineFailure = leafFailure,
        };
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }
        }, name: WorkerName);
        engine.Attach(worker);

        // Act: past the window the first give-up fails in the leaf; the leaf then recovers, and a
        // later failed pass asks again once the failed give-up ended.
        worker.RunIteration(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        worker.RunIteration(CancellationToken.None);
        bool recorded = await Eventually(() => ReferenceEquals(worker.Fault, leafFailure));
        var state = engine.State;
        bool engineWide = engine.HasEngineWideFailure;
        bool failing = engine.HasFailingWorker("a");
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
        engineWide.ShouldBeTrue();
        failing.ShouldBeTrue();
        engine.TakenOffline.ShouldBe(["a"]);
        engine.TakeOfflineCalls.ShouldBeGreaterThanOrEqualTo(2);
        engine.GiveUps.ShouldHaveSingleItem().Taken.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the worker failure window and minimum are the constructor's, 100 s and three passes by default")]
    public async Task Constructor_WorkerFailureWindow_ShouldBeTheConstructorsWithDefaults()
    {
        // Act
        await using var defaulted = new TestEngine(EngineName);
        await using var custom = new TestEngine(EngineName, workerFailureWindow: TimeSpan.FromSeconds(30), workerFailureMinimumPasses: 5);
        await using var longest = new TestEngine(EngineName, workerFailureWindow: DatabaseEngine.MaximumWorkerFailureWindow, workerFailureMinimumPasses: int.MaxValue);

        // Assert: owner decision 42 of 2026-10-07, Neo4j's window of about a hundred seconds, over
        // at least three failed passes.
        DatabaseEngine.DefaultWorkerFailureWindow.ShouldBe(TimeSpan.FromSeconds(100));
        DatabaseEngine.DefaultWorkerFailureMinimumPasses.ShouldBe(3);
        DatabaseEngine.MaximumWorkerFailureWindow.ShouldBe(TimeSpan.FromMilliseconds(int.MaxValue));
        defaulted.WorkerFailureWindow.ShouldBe(TimeSpan.FromSeconds(100));
        defaulted.WorkerFailureMinimumPasses.ShouldBe(3);
        custom.WorkerFailureWindow.ShouldBe(TimeSpan.FromSeconds(30));
        custom.WorkerFailureMinimumPasses.ShouldBe(5);
        longest.WorkerFailureWindow.ShouldBe(DatabaseEngine.MaximumWorkerFailureWindow);
        longest.WorkerFailureMinimumPasses.ShouldBe(int.MaxValue);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Engine: a window that is not positive or past the maximum, or a minimum below one pass, is refused")]
    [InlineData(0L, 3, "workerFailureWindow")]
    [InlineData(-1L, 3, "workerFailureWindow")]
    [InlineData(-1_000_000_000L, 3, "workerFailureWindow")]
    [InlineData(1L + int.MaxValue * TimeSpan.TicksPerMillisecond, 3, "workerFailureWindow")]
    [InlineData(1_000_000_000L, 0, "workerFailureMinimumPasses")]
    [InlineData(1_000_000_000L, -1, "workerFailureMinimumPasses")]
    public void Constructor_InvalidWorkerFailureWindow_ShouldThrow(long windowTicks, int minimumPasses, string parameter)
    {
        // Act
        var error = Should.Throw<ArgumentOutOfRangeException>(
            () => new TestEngine(EngineName, workerFailureWindow: TimeSpan.FromTicks(windowTicks), workerFailureMinimumPasses: minimumPasses));

        // Assert
        error.ParamName.ShouldBe(parameter);
    }

    /// <summary>
    /// The engine tells one database's failure from its own (owner decision 42 of 2026-10-07): a
    /// worker's failure of database a names a, never b, and not the engine as a whole, until a pass
    /// finishes a's work; a pass that fails as a whole is the engine's, names no database, and ends
    /// with the next pass that runs to its end.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: a worker's failure of one database names that database alone; a pass that fails as a whole is the engine's")]
    public async Task HasFailingWorker_OneDatabaseFails_ShouldNameItAloneUntilItsWorkFinishes()
    {
        // Arrange: a scripted pass that fails a, fails as a whole, or does every database's work.
        await using var engine = new TestEngine(EngineName);
        await engine.CreateDatabaseAsync("a");
        await engine.CreateDatabaseAsync("b");
        string script = "fail a";
        var worker = new ScriptedWorker((self, _) =>
        {
            self.Begin("a");
            self.Begin("b");
            switch (script)
            {
                case "fail a":
                    self.Fail("a", new InvalidOperationException("a failed"), TimeSpan.Zero);
                    break;
                case "throw":
                    throw new InvalidOperationException("the pass failed");
            }
        }, name: WorkerName);
        engine.Attach(worker);
        bool before = engine.HasFailingWorker("a");

        // Act
        worker.RunIteration(CancellationToken.None);
        var aFailing = (A: engine.HasFailingWorker("a"), B: engine.HasFailingWorker("b"), EngineWide: engine.HasEngineWideFailure, engine.State);
        script = "finish";
        worker.RunIteration(CancellationToken.None);
        var aFinished = (A: engine.HasFailingWorker("a"), engine.State);
        script = "throw";
        worker.RunIteration(CancellationToken.None);
        var passFailed = (A: engine.HasFailingWorker("a"), B: engine.HasFailingWorker("b"), EngineWide: engine.HasEngineWideFailure, engine.State);
        script = "finish";
        worker.RunIteration(CancellationToken.None);
        var passFinished = (EngineWide: engine.HasEngineWideFailure, engine.State);
        var empty = Should.Throw<ArgumentException>(() => engine.HasFailingWorker(default));

        // Assert
        before.ShouldBeFalse();
        aFailing.ShouldBe((true, false, false, EngineState.Faulted));
        aFinished.ShouldBe((false, EngineState.Running));
        passFailed.ShouldBe((false, false, true, EngineState.Faulted));
        passFinished.ShouldBe((false, EngineState.Running));
        empty.ParamName.ShouldBe("name");
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
