using System;
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
/// database the engine no longer holds open is not counted. The model engines' suites pin the
/// same through real storages.
/// </summary>
public sealed class DatabaseWorkerFailureLimitTests
{
    private const string EngineName = "test-engine";
    private const string WorkerName = EngineName + "/checkpoint";

    [Fact(DisplayName = "Cohesion Test [Database] - Worker failure limit: a database failing the limit of passes in a row is taken offline through its engine, and only it")]
    public async Task ReportFailure_LimitOfPassesInARow_ShouldTakeOnlyThatDatabaseOffline()
    {
        // Arrange: database a fails every pass, b never does; the engine allows three failed passes.
        await using var engine = new TestEngine(EngineName, workerFailureLimit: 3);
        await engine.CreateDatabaseAsync("a");
        await engine.CreateDatabaseAsync("b");
        var failure = new InvalidOperationException("checkpoint failed");
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("a"))
            {
                self.Fail("a", failure, TimeSpan.Zero);
            }

            self.Begin("b").ShouldBeTrue();
        }, name: WorkerName);
        engine.Attach(worker);

        // Act
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        var afterTwo = engine.GiveUps;
        worker.RunIteration(CancellationToken.None);
        var afterThree = engine.GiveUps;
        worker.RunIteration(CancellationToken.None);

        // Assert: the third failed pass gives up on a; the fourth asks again, and the leaf refuses
        // a database it already took offline.
        afterTwo.ShouldBeEmpty();
        var giveUp = afterThree.ShouldHaveSingleItem();
        giveUp.Name.ShouldBe("a");
        giveUp.Cause.ShouldBe(StorageOfflineCause.CheckpointFailures);
        giveUp.Failure.ShouldBeSameAs(failure);
        giveUp.Taken.ShouldBeTrue();
        giveUp.Reason.ShouldContain($"'{WorkerName}'");
        giveUp.Reason.ShouldContain("database 'a'");
        giveUp.Reason.ShouldContain("3 passes in a row");
        engine.TakenOffline.ShouldBe(["a"]);
        engine.GiveUps.Count.ShouldBe(2);
        engine.GiveUps[1].Taken.ShouldBeFalse();
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

        // Assert: two passes, not four reports, reach the limit; the second report of the
        // second pass asks again and is refused.
        afterOne.ShouldBeEmpty();
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

        // Assert
        if (cause is { } expected)
        {
            engine.GiveUps[0].Cause.ShouldBe(expected);
            engine.TakenOffline.ShouldBe(["a"]);
        }
        else
        {
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

        bool taken = worker.GiveUp("a", StorageOfflineCause.CheckpointFailures, "the test gives up", new InvalidOperationException("failed"));

        // Assert
        worker.ConsecutiveFailures.ShouldBe(DatabaseEngine.DefaultWorkerFailureLimit * 2);
        taken.ShouldBeFalse();
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

        // Act
        bool first = worker.RunIteration(CancellationToken.None);
        bool second = worker.RunIteration(CancellationToken.None);

        // Assert: asked on every failed pass, refused each time; the passes are the worker's own.
        first.ShouldBeFalse();
        second.ShouldBeFalse();
        engine.GiveUps.Count.ShouldBe(2);
        engine.GiveUps.ShouldAllBe(giveUp => !giveUp.Taken);
        engine.TakenOffline.ShouldBeEmpty();
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

        // Assert
        engine.GiveUps.ShouldBeEmpty();
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
        bool taken = worker.GiveUp("a", StorageOfflineCause.JournalSizeLimit, "its journal passed the cap", failure);
        bool again = worker.GiveUp("a", StorageOfflineCause.JournalSizeLimit, "its journal passed the cap", failure);

        // Assert
        deviceCause.ParamName.ShouldBe("cause");
        taken.ShouldBeTrue();
        again.ShouldBeFalse();
        var giveUp = engine.GiveUps[0];
        giveUp.Cause.ShouldBe(StorageOfflineCause.JournalSizeLimit);
        giveUp.Reason.ShouldBe("its journal passed the cap");
        giveUp.Failure.ShouldBeSameAs(failure);
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
}
