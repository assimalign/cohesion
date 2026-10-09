using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// The blob engine builder composes through the engine's compose method over the root
/// <see cref="DatabaseEngine"/> base (concrete-types plan, step P4.0 and phase 4, #1260): the
/// factories run one at a time as their products are attached, the base makes the attach checks
/// (among them the worker-name uniqueness the blob engine never checked) and names each pump
/// thread for its worker, and the shared builder state disposes whatever a failed build leaves
/// unowned and fails a compose method that breaks its contract.
/// </summary>
public sealed class BlobEngineCompositionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: products attach in order, a later factory sees the earlier product, and the engine and builder freeze")]
    public async Task Build_WithWorkerAndServerFactories_ShouldAttachInOrderAndFreeze()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("composed");
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
        engine.Workers.Count.ShouldBe(6);
        engine.Workers.Skip(4).ShouldBe(new DatabaseEngineWorker[] { first, second.ShouldNotBeNull() });
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

    /// <summary>
    /// The root engine base runs each worker's pump on a thread named for the worker (concrete-types
    /// plan §6.4, engine composition): the blob engine named it <c>{engine}/{kind}</c>, which here
    /// would have been <c>pumps/IndexMaintenance</c> for both workers.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: each worker's pump thread is named for the worker")]
    public async Task Build_WorkersOfOneKind_ShouldPumpEachOnAThreadNamedForIt()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("pumps");
        RecordingWorker? first = null;
        RecordingWorker? second = null;
        builder.AddWorker(engine => first = new RecordingWorker(engine, engine.Name + "/first"));
        builder.AddWorker(engine => second = new RecordingWorker(engine, engine.Name + "/second"));

        // Act
        await using var engine = builder.Build();
        bool started = first.ShouldNotBeNull().Started.Wait(TimeSpan.FromSeconds(10))
            && second.ShouldNotBeNull().Started.Wait(TimeSpan.FromSeconds(10));

        // Assert
        started.ShouldBeTrue();
        first.Kind.ShouldBe(second!.Kind);
        first.ThreadName.ShouldBe("pumps/first");
        second.ThreadName.ShouldBe("pumps/second");
        engine.Workers.Take(4).Select(worker => worker.Name).ShouldBe(new[]
        {
            "pumps/wal-flush",
            "pumps/page-writeback",
            "pumps/checkpoint",
            "pumps/version-purge",
        });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: each factory sees every product attached before it, and the server factories run after every worker")]
    public async Task Build_WorkerAndServerFactories_ShouldSeeEveryProductAttachedBeforeThem()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: an engine created without its builder takes no worker or server")]
    public async Task Create_WithoutBuilder_ShouldCompleteCompositionAtOnce()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions { EngineName = "standalone" });
        var worker = new RecordingWorker(engine);

        // Act
        var refusal = Should.Throw<InvalidOperationException>(() => engine.Compose([worker], []));

        // Assert
        refusal.Message.ShouldStartWith("Engine composition is frozen", Case.Sensitive);
        engine.Workers.Count.ShouldBe(4);
        engine.Workers.ShouldNotContain(worker);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: a server returned twice is refused and released once, by the engine")]
    public void Build_RepeatedServer_ShouldBeRefusedAndReleasedOnceByTheEngine()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        RecordingServer? server = null;
        BlobDatabaseEngine? product = null;
        builder.AddServer(engine => server = new RecordingServer(product = engine));
        builder.AddServer(_ => server!);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert: the engine owned the server, so only its disposal released it.
        failure.Message.ShouldBe("A composition product cannot be registered twice.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: a built-in worker a factory returns is refused and left to the engine")]
    public void Build_BuiltInWorkerReturned_ShouldBeRefusedAndLeftToTheEngine()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        BlobDatabaseEngine? product = null;
        builder.AddWorker(engine => (product = engine).Workers[2]);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe("A composition product cannot be registered twice.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: a server for another engine is refused and released without touching that engine")]
    public async Task Build_ServerForAnotherEngine_ShouldBeRefusedAndReleased()
    {
        // Arrange
        await using var other = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions { EngineName = "other" });
        var server = new RecordingServer(other);
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        BlobDatabaseEngine? product = null;
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: a refused server that fails to release is reported with the refusal, and the engine is disposed")]
    public async Task Build_RefusedServerFailsToRelease_ShouldAggregateTheRefusalAndTheCleanup()
    {
        // Arrange
        await using var other = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions { EngineName = "other" });
        var server = new RecordingServer(other) { StopFailure = new InvalidOperationException("The listener would not close.") };
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        BlobDatabaseEngine? product = null;
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

    /// <summary>
    /// The root engine base refuses a worker whose name another worker of the engine has, built-in
    /// or custom, ordinal and ignoring case (concrete-types plan §6.4, engine composition): the
    /// blob engine never checked worker names. The refused worker and the engine are disposed.
    /// </summary>
    /// <param name="duplicate">The name of the second worker: a built-in worker's, or the first custom worker's, in another case.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Composition: a worker named like another worker, built-in or custom, is refused, disposed, and the engine disposed")]
    [InlineData("NAMED/Checkpoint")]
    [InlineData("named/CUSTOM")]
    public void Build_DuplicateWorkerName_ShouldBeRefusedAndDisposed(string duplicate)
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("named");
        RecordingWorker? custom = null;
        RecordingWorker? worker = null;
        BlobDatabaseEngine? product = null;
        builder.AddWorker(engine => custom = new RecordingWorker(product = engine, "named/custom"));
        builder.AddWorker(engine => worker = new RecordingWorker(engine, duplicate));

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert: names compare ordinal, ignoring case.
        failure.Message.ShouldBe($"Worker name '{duplicate}' is already registered.");
        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        custom.ShouldNotBeNull().Disposals.ShouldBe(1);
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: a worker with a blank name fails inside its factory, and the engine is disposed")]
    public void Build_BlankWorkerName_ShouldFailInsideTheFactory()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        BlobDatabaseEngine? product = null;
        builder.AddWorker(engine => new RecordingWorker(product = engine, " "));

        // Act / Assert: the worker's constructor refuses the name; there is no product to dispose.
        Should.Throw<ArgumentException>(() => builder.Build()).ParamName.ShouldBe("name");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Composition: a factory that returns null disposes the engine")]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_NullProduct_ShouldDisposeTheEngine(bool worker)
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        BlobDatabaseEngine? product = null;
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

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Composition: a compose method that breaks the builder state's contract fails the build, runs no later factory and releases every product once")]
    [InlineData("skips-servers", "The compose method returned before it attached every product.", 2, 0)]
    [InlineData("buffers-workers", "The compose method read past a product it did not attach.", 1, 0)]
    [InlineData("buffers-servers", "The compose method read past a product it did not attach.", 2, 1)]
    [InlineData("reads-workers-twice", "The compose method read a product sequence twice.", 2, 0)]
    [InlineData("servers-before-workers", "The compose method requested a server before it attached every worker.", 1, 0)]
    public void Complete_ComposeBreaksTheContract_ShouldFailAndReleaseEveryProductOnce(string scenario, string message, int workersMade, int serversMade)
    {
        // Arrange: the state the builder runs, against a leaf compose method misused on purpose.
        var state = new DatabaseEngineBuilderState<BlobDatabaseEngine>("contract");
        var engine = BlobDatabaseEngine.CreateUncomposed(new BlobDatabaseEngineOptions { EngineName = "contract" });
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
        var failure = Should.Throw<InvalidOperationException>(() => state.Complete(engine, compose, BlobDatabaseEngine.ReleaseRefusedWorkerAsync));

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

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Composition: a blank engine name is refused before the engine is created")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankEngineName_ShouldBeRefused(string name)
    {
        // Act
        var direct = Should.Throw<ArgumentException>(() => BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions { EngineName = name }));
        var builder = Should.Throw<ArgumentException>(() => BlobDatabaseEngine.CreateBuilder(name));

        // Assert: the blob engine accepted a blank name before the root base; the builder names
        // the engine once and refuses a blank name before anything is composed.
        direct.ParamName.ShouldBe(nameof(BlobDatabaseEngineOptions.EngineName));
        builder.ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: an engine name changed on the builder fails the build before the engine exists")]
    public void Build_EngineNameChanged_ShouldFailBeforeTheEngineExists()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("named");
        BlobDatabaseEngine? product = null;
        builder.AddWorker(engine => new RecordingWorker(product = engine));
        string seeded = builder.EngineName!;
        builder.EngineName = "renamed";

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        seeded.ShouldBe("named");
        builder.Name.ShouldBe("named");
        failure.Message.ShouldStartWith("The engine builder for 'named' has its options' EngineName set to 'renamed'.", Case.Sensitive);
        product.ShouldBeNull();
    }

    /// <summary>
    /// A component that fails to close is reported in the root engine base's one aggregate, "One or
    /// more components of engine '{name}' failed to close." (concrete-types plan §6.4), for the
    /// blob engine's former "One or more blob engine components failed to close.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Composition: a component that fails to close is reported in the engine's one aggregate, and the rest still close")]
    public async Task DisposeAsync_ServerFailsToClose_ShouldReportTheEngineAggregate()
    {
        // Arrange
        var builder = BlobDatabaseEngine.CreateBuilder("closing");
        RecordingServer? server = null;
        RecordingWorker? worker = null;
        builder.AddWorker(engine => worker = new RecordingWorker(engine));
        builder.AddServer(engine => server = new RecordingServer(engine) { StopFailure = new InvalidOperationException("The listener would not close.") });
        var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("closing");

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
