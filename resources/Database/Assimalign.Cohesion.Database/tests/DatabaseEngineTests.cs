using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The root <see cref="DatabaseEngine"/> base (concrete-types plan §6.5, phase 3, #1259): the
/// field-backed identity, the attach semantics frozen after composition, the worker pump and the
/// state it folds (#1268), the disposal order servers, worker pumps, workers, databases, and every
/// guard of the public members.
/// </summary>
public class DatabaseEngineTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the name and model are the constructor's, and the name is required")]
    public void Constructor_NameAndModel_ShouldBeFixed()
    {
        // Arrange / Act
        using var engine = new TestEngine("orders-engine");

        // Assert
        engine.Name.ShouldBe("orders-engine");
        engine.Model.ShouldBe(EngineModel.Sql);
        engine.State.ShouldBe(EngineState.Running);
        engine.Workers.ShouldBeEmpty();
        engine.Servers.ShouldBeEmpty();
        Should.Throw<ArgumentException>(() => new TestEngine(" "));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an attached worker is owned, listed and pumped on its own thread")]
    public async Task AttachWorker_Composing_ShouldListAndPumpTheWorker()
    {
        // Arrange
        var log = new TestLog();
        await using var engine = new TestEngine(log: log);
        var worker = new RecordingWorker(log, "test-engine/maintenance");
        var before = engine.Workers;

        // Act
        engine.Attach(worker);
        bool pumping = worker.Waiting.Wait(_timeout);

        // Assert
        pumping.ShouldBeTrue();
        engine.Workers.ShouldHaveSingleItem().ShouldBeSameAs(worker);
        before.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an attach after composition completed is refused")]
    public async Task Attach_AfterCompleteComposition_ShouldThrow()
    {
        // Arrange
        await using var engine = new TestEngine();
        var worker = new ScriptedWorker((_, _) => { });
        var server = new TestServer(engine);
        engine.Complete();
        engine.Complete();

        // Act
        var workerError = Should.Throw<InvalidOperationException>(() => engine.Attach(worker));
        var serverError = Should.Throw<InvalidOperationException>(() => engine.Attach(server));

        // Assert
        workerError.ShouldNotBeOfType<ObjectDisposedException>();
        serverError.Message.ShouldBe("Engine composition is frozen; workers and servers attach only before it completes.");
        engine.Workers.ShouldBeEmpty();
        engine.Servers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an attach after disposal is refused as disposed")]
    public async Task Attach_AfterDispose_ShouldThrowObjectDisposed()
    {
        // Arrange
        var engine = new TestEngine();
        var server = new TestServer(engine);
        await engine.DisposeAsync();

        // Act / Assert
        Should.Throw<ObjectDisposedException>(() => engine.Attach(new ScriptedWorker((_, _) => { })));
        Should.Throw<ObjectDisposedException>(() => engine.Attach(server));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: a product is attached once, worker names are unique, and a server must front its engine")]
    public async Task Attach_InvalidProducts_ShouldThrow()
    {
        // Arrange
        await using var engine = new TestEngine();
        await using var other = new TestEngine("other-engine");
        var worker = new ScriptedWorker((_, _) => { }, name: "test-engine/checkpoint");
        var server = new TestServer(engine);
        engine.Attach(worker);
        engine.Attach(server);

        // Act
        var sameWorker = Should.Throw<InvalidOperationException>(() => engine.Attach(worker));
        var sameName = Should.Throw<InvalidOperationException>(() => engine.Attach(new ScriptedWorker((_, _) => { }, name: "TEST-ENGINE/CHECKPOINT")));
        var sameServer = Should.Throw<InvalidOperationException>(() => engine.Attach(server));
        var foreign = Should.Throw<InvalidOperationException>(() => engine.Attach(new TestServer(other)));

        // Assert
        sameWorker.Message.ShouldBe("A composition product cannot be registered twice.");
        sameName.Message.ShouldBe("Worker name 'TEST-ENGINE/CHECKPOINT' is already registered.");
        sameServer.Message.ShouldBe("A composition product cannot be registered twice.");
        foreign.Message.ShouldBe("A nested server must front its owning engine.");
        Should.Throw<ArgumentNullException>(() => engine.Attach((DatabaseEngineWorker)null!));
        Should.Throw<ArgumentNullException>(() => engine.Attach((DatabaseServer)null!));
        engine.Workers.ShouldHaveSingleItem();
        engine.Servers.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: disposal disposes servers, stops every pump, disposes workers, then closes the databases")]
    public async Task DisposeAsync_Composed_ShouldDisposeInTheBaseOrder()
    {
        // Arrange
        var log = new TestLog();
        var engine = new TestEngine(log: log);
        var first = new RecordingWorker(log, "test-engine/first");
        var second = new RecordingWorker(log, "test-engine/second");
        engine.Attach(first);
        engine.Attach(second);
        engine.Attach(new TestServer(engine, log, "server-a"));
        engine.Attach(new TestServer(engine, log, "server-b"));
        engine.Complete();
        await engine.CreateDatabaseAsync("appdb");
        first.Waiting.Wait(_timeout).ShouldBeTrue();
        second.Waiting.Wait(_timeout).ShouldBeTrue();

        // Act
        await engine.DisposeAsync();
        await engine.DisposeAsync();
        engine.Dispose();

        // Assert
        var entries = log.Entries;
        entries.Take(2).ShouldBe(new[] { "server-b:stop", "server-a:stop" });
        entries.Skip(2).Take(2).ShouldBe(new[] { "test-engine/first:stopped", "test-engine/second:stopped" }, ignoreOrder: true);
        entries.Skip(4).ShouldBe(new[]
        {
            "test-engine/second:dispose",
            "test-engine/first:dispose",
            "engine:databases",
            "database:appdb:dispose",
        });
        first.Disposes.ShouldBe(1);
        engine.State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: disposal runs every step past failures and reports them together")]
    public async Task DisposeAsync_StepsFail_ShouldRunEveryStepAndAggregate()
    {
        // Arrange
        var log = new TestLog();
        var serverFailure = new InvalidOperationException("server failed to stop");
        var workerFailure = new InvalidOperationException("worker failed to close");
        var coreFailure = new InvalidOperationException("database failed to close");
        var engine = new TestEngine(log: log) { DisposeFailure = coreFailure };
        var worker = new RecordingWorker(log, "test-engine/worker") { DisposeFailure = workerFailure };
        engine.Attach(worker);
        engine.Attach(new TestServer(engine, log) { StopFailure = serverFailure });

        // Act
        var error = await Should.ThrowAsync<AggregateException>(async () => await engine.DisposeAsync());

        // Assert
        error.InnerExceptions.ShouldBe(new Exception[] { serverFailure, workerFailure, coreFailure });
        log.Entries.ShouldContain("engine:databases");
        worker.Disposes.ShouldBe(1);
        engine.State.ShouldBe(EngineState.Disposed);
    }

    /// <summary>
    /// The worker base's release (concrete-types plan, row 7): an engine releases every worker it
    /// owns once, through the worker's release hook, after the worker's pump stopped. The worker has
    /// no public disposal; the base's protected release, which a model's builder reaches through its
    /// engine's internal re-exposure, leaves a worker an engine owns to the engine, and releases a
    /// worker no engine owns, once. The engine no longer type-tests its workers for disposal
    /// interfaces.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: a worker the engine owns is released by the engine only; one no engine owns is released once by the base's release")]
    public async Task DisposeAsync_Worker_ShouldBeReleasedOnceByItsOwner()
    {
        // Arrange
        var log = new TestLog();
        var engine = new TestEngine(log: log);
        var owned = new RecordingWorker(log, "test-engine/owned");
        var unowned = new RecordingWorker(log, "test-engine/unowned");
        engine.Attach(owned);
        engine.Complete();
        owned.Waiting.Wait(_timeout).ShouldBeTrue();

        // Act
        await TestEngine.Release(owned);
        int ownedBeforeEngine = owned.Disposes;
        await engine.DisposeAsync();
        await TestEngine.Release(owned);
        await TestEngine.Release(unowned);
        await TestEngine.Release(unowned);

        // Assert
        ownedBeforeEngine.ShouldBe(0);
        owned.Disposes.ShouldBe(1);
        unowned.Disposes.ShouldBe(1);
        var entries = log.Entries;
        Array.IndexOf(entries, "test-engine/owned:stopped").ShouldBeGreaterThanOrEqualTo(0);
        Array.IndexOf(entries, "test-engine/owned:stopped").ShouldBeLessThan(Array.IndexOf(entries, "test-engine/owned:dispose"));
        engine.Workers.ShouldNotContain(unowned);
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(DatabaseEngineWorker)).ShouldBeFalse();
        typeof(IDisposable).IsAssignableFrom(typeof(DatabaseEngineWorker)).ShouldBeFalse();
    }

    /// <summary>
    /// A worker belongs to one engine (concrete-types plan, row 7): the engine claims it before it
    /// starts the worker's pump, so a worker released before the attach is refused rather than
    /// pumped with its release hook already run, and the refused worker is not released again.
    /// Before the claim moved ahead of the pump's start, the engine accepted and pumped it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an attach of a released worker is refused, and the worker is not pumped")]
    public async Task AttachWorker_ReleasedWorker_ShouldBeRefused()
    {
        // Arrange
        var log = new TestLog();
        await using var engine = new TestEngine(log: log);
        var worker = new RecordingWorker(log, "test-engine/released");
        await TestEngine.Release(worker);

        // Act
        var refusal = Should.Throw<InvalidOperationException>(() => engine.Attach(worker));
        await TestEngine.Release(worker);

        // Assert
        refusal.Message.ShouldContain("belongs to one engine");
        engine.Workers.ShouldBeEmpty();
        worker.Waiting.IsSet.ShouldBeFalse();
        worker.Disposes.ShouldBe(1);
    }

    /// <summary>
    /// A worker belongs to one engine (concrete-types plan, row 7): one another engine owns is
    /// refused by a second engine's attach, and neither the second engine nor the base's release
    /// releases it; its owner releases it once, at its own disposal. Before the claim, the second
    /// engine pumped it too, and the first engine's disposal released it while the second engine's
    /// pump still ran it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an attach of a worker another engine owns is refused, and only its owner releases it")]
    public async Task AttachWorker_WorkerAnotherEngineOwns_ShouldBeRefused()
    {
        // Arrange
        var log = new TestLog();
        var owner = new TestEngine("owner-engine", log);
        await using var other = new TestEngine("other-engine", log);
        var worker = new RecordingWorker(log, "owner-engine/shared");
        owner.Attach(worker);
        worker.Waiting.Wait(_timeout).ShouldBeTrue();

        // Act
        var refusal = Should.Throw<InvalidOperationException>(() => other.Attach(worker));
        await TestEngine.Release(worker);
        await other.DisposeAsync();
        int beforeOwner = worker.Disposes;
        await owner.DisposeAsync();

        // Assert
        refusal.Message.ShouldContain("belongs to one engine");
        other.Workers.ShouldBeEmpty();
        beforeOwner.ShouldBe(0);
        worker.Disposes.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the state is Faulted exactly while a worker holds a failure")]
    public async Task State_WorkerFailure_ShouldBeFaultedUntilTheWorkerRecovers()
    {
        // Arrange
        await using var engine = new TestEngine();
        var failing = true;
        var worker = new ScriptedWorker((self, _) =>
        {
            if (self.Begin("appdb") && failing)
            {
                self.Fail("appdb", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            }
        });
        engine.Attach(worker);

        // Act
        worker.RunIteration(CancellationToken.None);
        var faulted = engine.State;
        failing = false;
        worker.RunIteration(CancellationToken.None);

        // Assert
        faulted.ShouldBe(EngineState.Faulted);
        engine.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the database members check the name, the engine and the token before the core runs")]
    public async Task DatabaseMembers_Guards_ShouldNotCallTheCore()
    {
        // Arrange
        var engine = new TestEngine();
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act / Assert: an empty name, then a canceled token.
        await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(default(DatabaseName)));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.OpenDatabaseAsync(default(DatabaseName)));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.DropDatabaseAsync(default(DatabaseName)));
        Should.Throw<ArgumentException>(() => engine.TryGetDatabase(default(DatabaseName), out _));
        await Should.ThrowAsync<OperationCanceledException>(async () => await engine.CreateDatabaseAsync("appdb", source.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await engine.OpenDatabaseAsync("appdb", source.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await engine.DropDatabaseAsync("appdb", source.Token));
        Should.Throw<OperationCanceledException>(() => engine.GetDatabasesAsync(source.Token));
        engine.CoreCalls.ShouldBe(0);

        // Act / Assert: a disposed engine.
        await engine.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.CreateDatabaseAsync("appdb"));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.OpenDatabaseAsync("appdb"));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.DropDatabaseAsync("appdb"));
        Should.Throw<ObjectDisposedException>(() => engine.TryGetDatabase("appdb", out _));
        Should.Throw<ObjectDisposedException>(() => engine.GetDatabasesAsync());
        engine.CoreCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the database members forward to the cores")]
    public async Task DatabaseMembers_Valid_ShouldForwardToTheCores()
    {
        // Arrange
        await using var engine = new TestEngine();

        // Act
        var created = await engine.CreateDatabaseAsync("appdb");
        await engine.CreateDatabaseAsync("audit");
        var opened = await engine.OpenDatabaseAsync("APPDB");
        bool found = engine.TryGetDatabase("appdb", out var fetched);
        var listed = new List<DatabaseInstance>();
        await foreach (var database in engine.GetDatabasesAsync())
        {
            listed.Add(database);
        }

        await engine.DropDatabaseAsync("audit");
        bool dropped = !engine.TryGetDatabase("audit", out _);

        // Assert
        opened.ShouldBeSameAs(created);
        found.ShouldBeTrue();
        fetched.ShouldBeSameAs(created);
        listed.Select(database => (string)database.Name).ShouldBe(new[] { "appdb", "audit" });
        dropped.ShouldBeTrue();
        created.Engine.ShouldBeSameAs(engine);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the offline list is the leaf's")]
    public async Task OfflineDatabases_LeafComputed_ShouldBeTheLeafsList()
    {
        // Arrange
        await using var engine = new TestEngine { Offline = [new DatabaseName("appdb")] };

        // Act / Assert
        engine.OfflineDatabases.ShouldHaveSingleItem().ShouldBe(new DatabaseName("appdb"));
    }

    /// <summary>
    /// A holder closes a database outside the engine: once the close ended the leaf no longer
    /// tracks it, the lookup does not find it, and an open opens the database again as a new
    /// instance (owner decision 33, #1289).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: a database a holder closed is forgotten and reopened by the next open")]
    public async Task OpenDatabaseAsync_DatabaseAHolderClosed_ShouldReopenIt()
    {
        // Arrange
        await using var engine = new TestEngine();
        var database = await engine.CreateDatabaseAsync("appdb");

        // Act
        await database.DisposeAsync();
        bool trackedAfterTheClose = engine.Tracks(database);
        bool found = engine.TryGetDatabase("appdb", out _);
        var reopened = await engine.OpenDatabaseAsync("appdb");

        // Assert
        engine.Forgets.ShouldBe(1);
        trackedAfterTheClose.ShouldBeFalse();
        found.ShouldBeFalse();
        reopened.ShouldNotBeSameAs(database);
        engine.Reopens.ShouldBe(1);
        engine.Tracks(reopened).ShouldBeTrue();
        engine.TryGetDatabase("appdb", out var fetched).ShouldBeTrue();
        fetched.ShouldBeSameAs(reopened);
    }

    /// <summary>
    /// An open that finds the database while a holder's close still runs waits for the close to
    /// end and opens the database again; it never hands out the closing instance. Meanwhile the
    /// lookup does not report the closing database.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an open during a holder's close waits for it, then reopens")]
    public async Task OpenDatabaseAsync_WhileAHolderClosesTheDatabase_ShouldWaitForTheCloseThenReopen()
    {
        // Arrange: every close holds at the gate.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new TestEngine { CloseGate = gate };
        using var released = new Release(() => gate.TrySetResult());
        var database = (TestDatabase)await engine.CreateDatabaseAsync("appdb");
        var close = database.DisposeAsync().AsTask();
        await database.Closing.WaitAsync(_timeout);

        // Act
        var open = engine.OpenDatabaseAsync("appdb").AsTask();
        bool foundWhileClosing = engine.TryGetDatabase("appdb", out _);
        bool openWaited = !await CompletesWithin(open, TimeSpan.FromMilliseconds(200));
        bool trackedWhileClosing = engine.Tracks(database);
        gate.SetResult();
        await close.WaitAsync(_timeout);
        var reopened = await open.WaitAsync(_timeout);

        // Assert
        foundWhileClosing.ShouldBeFalse();
        openWaited.ShouldBeTrue();
        trackedWhileClosing.ShouldBeTrue();
        reopened.ShouldNotBeSameAs(database);
        engine.Forgets.ShouldBe(1);
        engine.Reopens.ShouldBe(1);
        engine.Tracks(database).ShouldBeFalse();
    }

    /// <summary>
    /// An open waiting for a holder's close observes its token: canceled, it throws and leaves the
    /// close to end on its own, after which the database is forgotten as usual.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an open waiting for a close observes its token")]
    public async Task OpenDatabaseAsync_CanceledWhileWaitingForAClose_ShouldThrowAndLeaveTheClose()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new TestEngine { CloseGate = gate };
        using var released = new Release(() => gate.TrySetResult());
        var database = (TestDatabase)await engine.CreateDatabaseAsync("appdb");
        var close = database.DisposeAsync().AsTask();
        await database.Closing.WaitAsync(_timeout);
        using var source = new CancellationTokenSource();

        // Act
        var open = engine.OpenDatabaseAsync("appdb", source.Token).AsTask();
        source.CancelAfter(TimeSpan.FromMilliseconds(50));
        var canceled = await Should.ThrowAsync<OperationCanceledException>(async () => await open.WaitAsync(_timeout));
        gate.SetResult();
        await close.WaitAsync(_timeout);

        // Assert
        canceled.CancellationToken.ShouldBe(source.Token);
        engine.Forgets.ShouldBe(1);
        engine.Tracks(database).ShouldBeFalse();
        engine.Reopens.ShouldBe(0);
    }

    /// <summary>
    /// The leaf drops a database a holder is closing: its own disposal of the database waits for
    /// the holder's close, which does not wait for the leaf's lock, because the leaf let the
    /// database go first. Nothing deadlocks, and the database is closed once.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: a drop during a holder's close waits for it without deadlocking")]
    public async Task DropDatabaseAsync_WhileAHolderClosesTheDatabase_ShouldWaitForTheClose()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new TestEngine { CloseGate = gate };
        using var released = new Release(() => gate.TrySetResult());
        var database = (TestDatabase)await engine.CreateDatabaseAsync("appdb");
        var close = database.DisposeAsync().AsTask();
        await database.Closing.WaitAsync(_timeout);

        // Act: the leaf's drop core closes under its lock, so it runs off the test's thread.
        var drop = Task.Run(async () => await engine.DropDatabaseAsync("appdb"));
        bool dropWaited = !await CompletesWithin(drop, TimeSpan.FromMilliseconds(200));
        gate.SetResult();
        await drop.WaitAsync(_timeout);
        await close.WaitAsync(_timeout);

        // Assert
        dropWaited.ShouldBeTrue();
        database.AsyncDisposeCores.ShouldBe(1);
        database.DisposeCores.ShouldBe(0);
        engine.Forgets.ShouldBe(1);
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("appdb"));
    }

    /// <summary>
    /// The engine's disposal closes its databases after a holder's close of one of them ended:
    /// committed work is durable when the engine's disposal completes, whoever closed it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: disposal waits for a holder's close it finds running")]
    public async Task DisposeAsync_WhileAHolderClosesADatabase_ShouldWaitForThatClose()
    {
        // Arrange
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new TestEngine { CloseGate = gate };
        var database = (TestDatabase)await engine.CreateDatabaseAsync("appdb");
        var close = database.DisposeAsync().AsTask();
        await database.Closing.WaitAsync(_timeout);

        // Act
        var disposal = engine.DisposeAsync().AsTask();
        bool disposalWaited = !await CompletesWithin(disposal, TimeSpan.FromMilliseconds(200));
        gate.SetResult();
        await disposal.WaitAsync(_timeout);
        await close.WaitAsync(_timeout);

        // Assert
        disposalWaited.ShouldBeTrue();
        database.AsyncDisposeCores.ShouldBe(1);
        engine.State.ShouldBe(EngineState.Disposed);
    }

    /// <summary>
    /// A leaf that keeps tracking a closed database would hand the closed instance back forever;
    /// the open reports the defect instead of spinning.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Engine: an open refuses a closed instance the leaf never forgot")]
    public async Task OpenDatabaseAsync_LeafKeepsAClosedDatabase_ShouldRefuseRatherThanSpin()
    {
        // Arrange
        await using var engine = new TestEngine { KeepClosedDatabases = true };
        var database = await engine.CreateDatabaseAsync("appdb");
        await database.DisposeAsync();

        // Act
        var error = await Should.ThrowAsync<InvalidOperationException>(async () => await engine.OpenDatabaseAsync("appdb").AsTask().WaitAsync(_timeout));

        // Assert
        error.Message.ShouldBe("Engine 'test-engine' still tracks database 'appdb' after its close ended; the engine did not forget it.");
        engine.Forgets.ShouldBe(1);
    }

    private static async Task<bool> CompletesWithin(Task task, TimeSpan wait)
        => await Task.WhenAny(task, Task.Delay(wait)) == task;

    // Lets a held close end when a test leaves its scope, a failed assertion included. Declared
    // after the engine, it runs before the engine's disposal, which waits for that close.
    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
