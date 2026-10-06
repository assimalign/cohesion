using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// The key-value engine builder composes through the engine's compose method over the root
/// <see cref="DatabaseEngine"/> base (concrete-types plan, step P4.0 and phase 4, #1260): the
/// factories run one at a time as their products are attached, the base makes the attach checks,
/// and the shared builder state disposes whatever a failed build leaves unowned and fails a compose
/// method that breaks its contract. These are the first in-repository runs of that path.
/// </summary>
public sealed class KeyValueEngineCompositionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: products attach in order, a later factory sees the earlier product, and the engine and builder freeze")]
    public async Task Build_WithWorkerAndServerFactories_ShouldAttachInOrderAndFreeze()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        builder.EngineName = "composed";
        RecordingWorker? first = null;
        RecordingWorker? second = null;
        RecordingServer? server = null;
        bool secondSawFirst = false;
        builder.AddWorker(engine => first = new RecordingWorker(engine, engine.Name + "/first"));
        builder.AddWorker(engine =>
        {
            secondSawFirst = engine.Workers.Contains(first!);
            return second = new RecordingWorker(engine, engine.Name + "/second");
        });
        builder.AddServer(engine => server = new RecordingServer(engine));
        first.ShouldBeNull();
        server.ShouldBeNull();

        // Act
        var engine = builder.Build();
        bool started = first.ShouldNotBeNull().Started.Wait(TimeSpan.FromSeconds(10));
        var frozen = Should.Throw<InvalidOperationException>(() => engine.Compose([new RecordingWorker(engine, "late")], []));
        await engine.DisposeAsync();

        // Assert
        started.ShouldBeTrue();
        secondSawFirst.ShouldBeTrue();
        engine.Workers.Count.ShouldBe(7);
        engine.Workers.Skip(5).ShouldBe(new DatabaseEngineWorker[] { first, second.ShouldNotBeNull() });
        engine.Servers.ShouldHaveSingleItem().ShouldBeSameAs(server);
        server.ShouldNotBeNull().Engine.ShouldBeSameAs(engine);
        server.Starts.ShouldBe(0);
        frozen.Message.ShouldBe("Engine composition is frozen; workers and servers attach only before it completes.");
        Should.Throw<InvalidOperationException>(() => builder.EngineName = "late");
        Should.Throw<InvalidOperationException>(() => builder.AddWorker(_ => first!));
        Should.Throw<InvalidOperationException>(() => builder.Build());
        first.Disposals.ShouldBe(1);
        second.Disposals.ShouldBe(1);
        server.Stops.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: each factory sees every product attached before it, and the server factories run after every worker")]
    public async Task Build_WorkerAndServerFactories_ShouldSeeEveryProductAttachedBeforeThem()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        RecordingWorker? first = null;
        RecordingWorker? second = null;
        RecordingServer? server = null;
        List<string> observations = [];
        builder.AddWorker(engine => first = new RecordingWorker(engine, engine.Name + "/first"));
        builder.AddWorker(engine =>
        {
            observations.Add($"second worker: first attached={engine.Workers.Contains(first!)}");
            return second = new RecordingWorker(engine, engine.Name + "/second");
        });
        builder.AddServer(engine =>
        {
            observations.Add($"first server: workers attached={engine.Workers.Contains(first!) && engine.Workers.Contains(second!)}");
            return server = new RecordingServer(engine);
        });
        builder.AddServer(engine =>
        {
            observations.Add($"second server: first attached={engine.Servers.Contains(server!)}");
            return new RecordingServer(engine);
        });

        // Act
        await using var engine = builder.Build();

        // Assert
        observations.ShouldBe(new[]
        {
            "second worker: first attached=True",
            "first server: workers attached=True",
            "second server: first attached=True",
        });
        engine.Servers.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: an engine created without its builder takes no worker or server")]
    public async Task Create_WithoutBuilder_ShouldCompleteCompositionAtOnce()
    {
        // Arrange
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "standalone" });
        var worker = new RecordingWorker(engine);

        // Act
        var refusal = Should.Throw<InvalidOperationException>(() => engine.Compose([worker], []));

        // Assert
        refusal.Message.ShouldStartWith("Engine composition is frozen", Case.Sensitive);
        engine.Workers.Count.ShouldBe(5);
        engine.Workers.ShouldNotContain(worker);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a server returned twice is refused and released once, by the engine")]
    public void Build_RepeatedServer_ShouldBeRefusedAndReleasedOnceByTheEngine()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        RecordingServer? server = null;
        KeyValueDatabaseEngine? product = null;
        builder.AddServer(engine => server = new RecordingServer(product = engine));
        builder.AddServer(_ => server!);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert: the engine owned the server, so only its disposal released it.
        failure.Message.ShouldBe("A composition product cannot be registered twice.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a built-in worker a factory returns is refused and left to the engine")]
    public void Build_BuiltInWorkerReturned_ShouldBeRefusedAndLeftToTheEngine()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        KeyValueDatabaseEngine? product = null;
        builder.AddWorker(engine => (product = engine).Workers[2]);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe("A composition product cannot be registered twice.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a server for another engine is refused and released without touching that engine")]
    public async Task Build_ServerForAnotherEngine_ShouldBeRefusedAndReleased()
    {
        // Arrange
        await using var other = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "other" });
        var server = new RecordingServer(other);
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        KeyValueDatabaseEngine? product = null;
        builder.AddServer(engine => { product = engine; return server; });

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe("A nested server must front its owning engine.");
        server.Stops.ShouldBe(1);
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        other.State.ShouldBe(EngineState.Running);
        other.Servers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a refused server that fails to release is reported with the refusal, and the engine is disposed")]
    public async Task Build_RefusedServerFailsToRelease_ShouldAggregateTheRefusalAndTheCleanup()
    {
        // Arrange
        await using var other = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "other" });
        var server = new RecordingServer(other) { StopFailure = new InvalidOperationException("The listener would not close.") };
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        KeyValueDatabaseEngine? product = null;
        builder.AddServer(engine => { product = engine; return server; });

        // Act
        var failure = Should.Throw<AggregateException>(() => builder.Build());

        // Assert: the refusal first, then the cleanup that failed.
        failure.InnerExceptions.Select(exception => exception.Message).ShouldBe(new[]
        {
            "A nested server must front its owning engine.",
            "The listener would not close.",
        });
        server.Stops.ShouldBe(1);
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        other.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a worker named like a built-in worker is refused, disposed, and the engine disposed")]
    public void Build_DuplicateWorkerName_ShouldBeRefusedAndDisposed()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        builder.EngineName = "named";
        RecordingWorker? worker = null;
        KeyValueDatabaseEngine? product = null;
        builder.AddWorker(engine => worker = new RecordingWorker(product = engine, "NAMED/Checkpoint"));

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert: names compare ordinal, ignoring case.
        failure.Message.ShouldBe("Worker name 'NAMED/Checkpoint' is already registered.");
        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a worker with a blank name fails inside its factory, and the engine is disposed")]
    public void Build_BlankWorkerName_ShouldFailInsideTheFactory()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        KeyValueDatabaseEngine? product = null;
        builder.AddWorker(engine => new RecordingWorker(product = engine, " "));

        // Act / Assert: the worker's constructor refuses the name; there is no product to dispose.
        Should.Throw<ArgumentException>(() => builder.Build()).ParamName.ShouldBe("name");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a factory that returns null disposes the engine")]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_NullProduct_ShouldDisposeTheEngine(bool worker)
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        KeyValueDatabaseEngine? product = null;
        if (worker)
        {
            builder.AddWorker(engine => { product = engine; return null!; });
        }
        else
        {
            builder.AddServer(engine => { product = engine; return null!; });
        }

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe(worker ? "A worker factory returned null." : "A server factory returned null.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a failing factory disposes the earlier products and the engine, and freezes the builder")]
    public void Build_FailingFactory_ShouldDisposeEarlierProductsAndFreezeTheBuilder()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        RecordingWorker? worker = null;
        RecordingServer? server = null;
        KeyValueDatabaseEngine? product = null;
        builder.AddWorker(engine => worker = new RecordingWorker(engine));
        builder.AddServer(engine => server = new RecordingServer(engine));
        builder.AddServer(engine =>
        {
            product = engine;
            throw new InvalidOperationException("Server construction failed.");
        });

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe("Server construction failed.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => builder.Build());
        Should.Throw<InvalidOperationException>(() => builder.RootPath = null);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a compose method that breaks the builder state's contract fails the build, runs no later factory and releases every product once")]
    [InlineData("skips-servers", "The compose method returned before it attached every product.", 2, 0)]
    [InlineData("buffers-workers", "The compose method read past a product it did not attach.", 1, 0)]
    [InlineData("buffers-servers", "The compose method read past a product it did not attach.", 2, 1)]
    [InlineData("reads-workers-twice", "The compose method read a product sequence twice.", 2, 0)]
    [InlineData("servers-before-workers", "The compose method requested a server before it attached every worker.", 1, 0)]
    public void Complete_ComposeBreaksTheContract_ShouldFailAndReleaseEveryProductOnce(string scenario, string message, int workersMade, int serversMade)
    {
        // Arrange: the state the builder runs, against a leaf compose method misused on purpose.
        var state = new DatabaseEngineBuilderState<KeyValueDatabaseEngine, DatabaseEngineWorker, DatabaseServer>();
        var engine = KeyValueDatabaseEngine.CreateUncomposed(new KeyValueDatabaseEngineOptions { EngineName = "contract" });
        List<RecordingWorker> workers = [];
        List<RecordingServer> servers = [];
        state.AddWorker(product => Made(workers, new RecordingWorker(product, product.Name + "/first")));
        state.AddWorker(product => Made(workers, new RecordingWorker(product, product.Name + "/second")));
        state.AddServer(product => Made(servers, new RecordingServer(product)));
        Action<IEnumerable<DatabaseEngineWorker>, IEnumerable<DatabaseServer>> compose = scenario switch
        {
            "skips-servers" => (w, _) => engine.Compose(w, []),
            "buffers-workers" => (w, s) => engine.Compose(w.ToList(), s),
            "buffers-servers" => (w, s) => engine.Compose(w, Buffered(s)),
            "reads-workers-twice" => (w, s) => engine.Compose(w.Concat(w), s),
            "servers-before-workers" => (w, s) => engine.Compose(w.Take(1), s),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => state.Complete(engine, compose, KeyValueDatabaseEngine.ReleaseRefusedWorkerAsync));

        // Assert: an attached product is released by the engine, an unattached one by the state.
        failure.Message.ShouldBe(message);
        engine.State.ShouldBe(EngineState.Disposed);
        workers.Count.ShouldBe(workersMade);
        servers.Count.ShouldBe(serversMade);
        workers.ShouldAllBe(worker => worker.Disposals == 1);
        servers.ShouldAllBe(server => server.Stops == 1);

        static T Made<T>(List<T> made, T product)
        {
            made.Add(product);
            return product;
        }

        // Buffers the sequence when it is first read, not when the compose method is called.
        static IEnumerable<T> Buffered<T>(IEnumerable<T> sequence)
        {
            foreach (var item in sequence.ToList())
            {
                yield return item;
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a configuration that builds and then fails does not leak its engine")]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKeyValue_ConfigurationThatBuildsPrematurely_ShouldNotLeakItsEngine(bool throwAfterBuild)
    {
        // Arrange
        var builder = new RecordingApplicationBuilder();
        KeyValueDatabaseEngine? product = null;
        builder.AddKeyValue((_, engine) =>
        {
            product = engine.Build();
            if (throwAfterBuild)
            {
                throw new InvalidOperationException("Configuration failed.");
            }
        });

        // Act / Assert: either the configuration's own failure, or the verb's second build.
        Should.Throw<InvalidOperationException>(() => builder.MaterializeEngine());
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a blank engine name is refused before the engine is created")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankEngineName_ShouldBeRefused(string name)
    {
        // Act
        var direct = Should.Throw<ArgumentException>(() => KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = name }));
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        builder.EngineName = name;
        var built = Should.Throw<ArgumentException>(() => builder.Build());

        // Assert
        direct.ParamName.ShouldBe(nameof(KeyValueDatabaseEngineOptions.EngineName));
        built.ParamName.ShouldBe(nameof(KeyValueDatabaseEngineOptions.EngineName));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Composition: a component that fails to close is reported in the engine's one aggregate, and the rest still close")]
    public async Task DisposeAsync_ServerFailsToClose_ShouldReportTheEngineAggregate()
    {
        // Arrange
        var builder = KeyValueDatabaseEngine.CreateBuilder();
        builder.EngineName = "closing";
        RecordingServer? server = null;
        RecordingWorker? worker = null;
        builder.AddWorker(engine => worker = new RecordingWorker(engine));
        builder.AddServer(engine => server = new RecordingServer(engine) { StopFailure = new InvalidOperationException("The listener would not close.") });
        var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("closing", TestTimeout.Token());

        // Act
        var failure = await Should.ThrowAsync<AggregateException>(async () => await engine.DisposeAsync());

        // Assert
        failure.Message.ShouldStartWith("One or more components of engine 'closing' failed to close.", Case.Sensitive);
        failure.InnerExceptions.ShouldHaveSingleItem().Message.ShouldBe("The listener would not close.");
        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        engine.State.ShouldBe(EngineState.Disposed);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await database.CreateSessionAsync());
    }
}
