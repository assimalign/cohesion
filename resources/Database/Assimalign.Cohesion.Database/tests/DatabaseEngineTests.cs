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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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
        bool pumping = worker.Waiting.Wait(Timeout);

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
        first.Waiting.Wait(Timeout).ShouldBeTrue();
        second.Waiting.Wait(Timeout).ShouldBeTrue();

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
        IDatabaseEngine bridged = engine;

        // Act / Assert
        engine.OfflineDatabases.ShouldHaveSingleItem().ShouldBe(new DatabaseName("appdb"));
        bridged.OfflineDatabases.ShouldBeSameAs(engine.OfflineDatabases);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Engine: the interface view is the same engine, workers, servers and databases")]
    public async Task InterfaceBridge_IDatabaseEngine_ShouldExposeTheSameObjects()
    {
        // Arrange
        var engine = new TestEngine();
        var worker = new ScriptedWorker((_, _) => { });
        var server = new TestServer(engine);
        engine.Attach(worker);
        engine.Attach(server);
        IDatabaseEngine bridged = engine;

        // Act
        var created = await bridged.CreateDatabaseAsync("appdb");
        var opened = await bridged.OpenDatabaseAsync("appdb");
        bool found = bridged.TryGetDatabase("appdb", out var fetched);
        bool missing = bridged.TryGetDatabase("missing", out var absent);
        var listed = new List<IDatabase>();
        await foreach (var database in bridged.GetDatabasesAsync())
        {
            listed.Add(database);
        }

        // Assert
        bridged.Name.ShouldBe(engine.Name);
        bridged.Model.ShouldBe(engine.Model);
        bridged.Workers.ShouldHaveSingleItem().ShouldBeSameAs(worker);
        bridged.Servers.ShouldHaveSingleItem().ShouldBeSameAs(server);
        opened.ShouldBeSameAs(created);
        found.ShouldBeTrue();
        fetched.ShouldBeSameAs(created);
        missing.ShouldBeFalse();
        absent.ShouldBeNull();
        listed.ShouldHaveSingleItem().ShouldBeSameAs(created);
        await bridged.DisposeAsync();
        bridged.State.ShouldBe(EngineState.Disposed);
    }
}
