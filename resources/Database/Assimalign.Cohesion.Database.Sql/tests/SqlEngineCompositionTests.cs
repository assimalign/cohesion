using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL engine builder composes through the engine's compose method over the root
/// <see cref="DatabaseEngine"/> base (concrete-types plan, step P4.0 and phase 4, #1260): the
/// factories run one at a time as their products are attached, the base makes the attach checks
/// (the worker-name uniqueness and the pump threads named for their workers, which the SQL
/// engine already had), and the shared builder state disposes whatever a failed build leaves
/// unowned and fails a compose method that breaks its contract.
/// </summary>
public sealed class SqlEngineCompositionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: products attach in order, a later factory sees the earlier product, and the engine and builder freeze")]
    public async Task Build_WithWorkerAndServerFactories_ShouldAttachInOrderAndFreeze()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("composed");
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
        Should.Throw<InvalidOperationException>(() => builder.AddDatabase("late"));
        Should.Throw<InvalidOperationException>(() => builder.AddWorker(_ => first!));
        Should.Throw<InvalidOperationException>(() => builder.Build());
        first.Disposals.ShouldBe(1);
        second.Disposals.ShouldBe(1);
        server.Stops.ShouldBe(1);
    }

    /// <summary>
    /// The root engine base runs each worker's pump on a thread named for the worker (concrete-types
    /// plan §6.4, engine composition), as the SQL engine already named its pumps, so two workers of
    /// one kind still pump on threads named apart.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: each worker's pump thread is named for the worker")]
    public async Task Build_WorkersOfOneKind_ShouldPumpEachOnAThreadNamedForIt()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("pumps");
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
        engine.Workers.Take(5).Select(worker => worker.Name).ShouldBe(new[]
        {
            "pumps/wal-flush",
            "pumps/page-writeback",
            "pumps/checkpoint",
            "pumps/version-purge",
            "pumps/index-maintenance",
        });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: each factory sees every product attached before it, and the server factories run after every worker")]
    public async Task Build_WorkerAndServerFactories_ShouldSeeEveryProductAttachedBeforeThem()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: an engine created without its builder takes no worker or server")]
    public async Task Create_WithoutBuilder_ShouldCompleteCompositionAtOnce()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "standalone" });
        var worker = new RecordingWorker(engine);

        // Act
        var refusal = Should.Throw<InvalidOperationException>(() => engine.Compose([worker], []));

        // Assert
        refusal.Message.ShouldStartWith("Engine composition is frozen", Case.Sensitive);
        engine.Workers.Count.ShouldBe(5);
        engine.Workers.ShouldNotContain(worker);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a server returned twice is refused and released once, by the engine")]
    public void Build_RepeatedServer_ShouldBeRefusedAndReleasedOnceByTheEngine()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        RecordingServer? server = null;
        SqlDatabaseEngine? product = null;
        builder.AddServer(engine => server = new RecordingServer(product = engine));
        builder.AddServer(_ => server!);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert: the engine owned the server, so only its disposal released it.
        failure.Message.ShouldBe("A composition product cannot be registered twice.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a built-in worker a factory returns is refused and left to the engine")]
    public void Build_BuiltInWorkerReturned_ShouldBeRefusedAndLeftToTheEngine()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        SqlDatabaseEngine? product = null;
        builder.AddWorker(engine => (product = engine).Workers[2]);

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe("A composition product cannot be registered twice.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a server for another engine is refused and released without touching that engine")]
    public async Task Build_ServerForAnotherEngine_ShouldBeRefusedAndReleased()
    {
        // Arrange
        await using var other = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "other" });
        var server = new RecordingServer(other);
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        SqlDatabaseEngine? product = null;
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a refused server that fails to release is reported with the refusal, and the engine is disposed")]
    public async Task Build_RefusedServerFailsToRelease_ShouldAggregateTheRefusalAndTheCleanup()
    {
        // Arrange
        await using var other = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "other" });
        var server = new RecordingServer(other) { StopFailure = new InvalidOperationException("The listener would not close.") };
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        SqlDatabaseEngine? product = null;
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
    /// or custom, ordinal and ignoring case (concrete-types plan §6.4, engine composition), with the
    /// message the SQL engine's own attach used. The refused worker and the engine are disposed.
    /// </summary>
    /// <param name="duplicate">The name of the second worker: a built-in worker's, or the first custom worker's, in another case.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Composition: a worker named like another worker, built-in or custom, is refused, disposed, and the engine disposed")]
    [InlineData("NAMED/Checkpoint")]
    [InlineData("named/CUSTOM")]
    public void Build_DuplicateWorkerName_ShouldBeRefusedAndDisposed(string duplicate)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("named");
        RecordingWorker? custom = null;
        RecordingWorker? worker = null;
        SqlDatabaseEngine? product = null;
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a worker with a blank name fails inside its factory, and the engine is disposed")]
    public void Build_BlankWorkerName_ShouldFailInsideTheFactory()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        SqlDatabaseEngine? product = null;
        builder.AddWorker(engine => new RecordingWorker(product = engine, " "));

        // Act / Assert: the worker's constructor refuses the name; there is no product to dispose.
        Should.Throw<ArgumentException>(() => builder.Build()).ParamName.ShouldBe("name");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Composition: a factory that returns null disposes the engine")]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_NullProduct_ShouldDisposeTheEngine(bool worker)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        SqlDatabaseEngine? product = null;
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
        failure.Message.ShouldBe(worker ? "Engine 'sql-engine': a worker factory returned null." : "Engine 'sql-engine': a server factory returned null.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Composition: a compose method that breaks the builder state's contract fails the build, runs no later factory and releases every product once")]
    [InlineData("skips-servers", "The compose method returned before it attached every product.", 2, 0)]
    [InlineData("buffers-workers", "The compose method read past a product it did not attach.", 1, 0)]
    [InlineData("buffers-servers", "The compose method read past a product it did not attach.", 2, 1)]
    [InlineData("reads-workers-twice", "The compose method read a product sequence twice.", 2, 0)]
    [InlineData("servers-before-workers", "The compose method requested a server before it attached every worker.", 1, 0)]
    public void Complete_ComposeBreaksTheContract_ShouldFailAndReleaseEveryProductOnce(string scenario, string message, int workersMade, int serversMade)
    {
        // Arrange: the state the builder runs, against a leaf compose method misused on purpose.
        var state = new DatabaseEngineBuilderState<SqlDatabaseEngine>("contract");
        var engine = SqlDatabaseEngine.CreateUncomposed(new SqlDatabaseEngineOptions { EngineName = "contract" });
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
        var failure = Should.Throw<InvalidOperationException>(() => state.Complete(engine, compose, SqlDatabaseEngine.ReleaseRefusedWorkerAsync));

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

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Composition: a blank engine name is refused before the engine is created")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankEngineName_ShouldBeRefused(string name)
    {
        // Act
        var direct = Should.Throw<ArgumentException>(() => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = name }));
        var builder = Should.Throw<ArgumentException>(() => SqlDatabaseEngine.CreateBuilder(name));
        var verb = Should.Throw<ArgumentException>(() => new RecordingApplicationBuilder().AddSql(name, _ => { }));

        // Assert: the SQL engine accepted a blank name before the root base; the builder and the
        // verb, which name the engine once, refuse it before anything is registered.
        direct.ParamName.ShouldBe(nameof(SqlDatabaseEngineOptions.EngineName));
        builder.ParamName.ShouldBe("name");
        verb.ParamName.ShouldBe("name");
    }

    /// <summary>
    /// Until the options lose <c>EngineName</c>, the builder seeds it with its name, and a build whose
    /// options name another engine is refused before anything is created (B1 of the engine
    /// extensibility design): the engine is named once.
    /// </summary>
    /// <param name="renamed">The options' engine name at Build.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Composition: options that name another engine than the builder fail the build")]
    [InlineData("other")]
    [InlineData("SQL-ENGINE")]
    [InlineData(null)]
    public void Build_OptionsNameAnotherEngine_ShouldFailBeforeTheEngineExists(string? renamed)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        SqlDatabaseEngine? product = null;
        builder.AddWorker(engine => new RecordingWorker(product = engine));
        string seeded = builder.Options.EngineName!;
        builder.Options.EngineName = renamed;

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        seeded.ShouldBe("sql-engine");
        builder.Name.ShouldBe("sql-engine");
        failure.Message.ShouldStartWith("The engine builder for 'sql-engine' has its options' EngineName set to ", Case.Sensitive);
        product.ShouldBeNull();
    }

    /// <summary>
    /// A component that fails to close is reported in the root engine base's one aggregate, "One or
    /// more components of engine '{name}' failed to close." (concrete-types plan §6.4), for the
    /// SQL engine's former "Engine disposal encountered failures.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a component that fails to close is reported in the engine's one aggregate, and the rest still close")]
    public async Task DisposeAsync_ServerFailsToClose_ShouldReportTheEngineAggregate()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("closing");
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a failing server factory disposes the engine and the builder refuses another build")]
    public void Build_FailingFactory_ShouldDisposeTheEngineAndForbidRetry()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        SqlDatabaseEngine? product = null;
        builder.AddServer(engine => { product = engine; throw new InvalidOperationException("factory"); });

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => builder.Build());
        var retry = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        failure.Message.ShouldBe("factory");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        retry.Message.ShouldBe("The builder of engine 'sql-engine' supports one build attempt.");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: AddSql disposes an engine its configuration built and abandoned")]
    public void AddSql_ConfigurationThatBuildsPrematurely_ShouldNotLeakItsEngine()
    {
        // Arrange
        var application = new RecordingApplicationBuilder();
        SqlDatabaseEngine? early = null;
        application.AddSql("premature", builder => early = builder.Build());

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => application.MaterializeEngine());

        // Assert
        failure.Message.ShouldBe("The builder of engine 'premature' supports one build attempt.");
        early.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    /// <summary>
    /// The builder state disposes a rejected product on the thread pool, so a server whose stop
    /// awaits never posts back to the caller's synchronization context, which a synchronous
    /// <see cref="SqlDatabaseEngineBuilder.Build"/> blocks.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: the cleanup of a failed build never posts to the caller's synchronization context")]
    public void Build_RefusedServerStopYields_ShouldNotUseTheCallersSynchronizationContext()
    {
        // Arrange
        using var other = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "other" });
        var rejected = new YieldingServer(other);
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        builder.AddServer(_ => rejected);
        var valid = SqlDatabaseEngine.CreateBuilder("sql-engine");
        var accepted = default(YieldingServer);
        valid.AddServer(engine => accepted = new YieldingServer(engine));
        var original = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectingSynchronizationContext());

            // Act
            var failure = Should.Throw<InvalidOperationException>(() => builder.Build());
            valid.Build().Dispose();

            // Assert
            failure.Message.ShouldBe("A nested server must front its owning engine.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        rejected.Stops.ShouldBe(1);
        accepted.ShouldNotBeNull().Stops.ShouldBe(1);
    }

    /// <summary>
    /// A cancellation callback that throws when the engine stops its pumps does not skip the joins:
    /// the engine waits for the worker's pump to end before it releases the worker, and reports the
    /// callback's failure in its aggregate.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Composition: a throwing cancellation callback still lets the engine join every pump before it releases the workers")]
    public async Task DisposeAsync_CancellationCallbackThrows_ShouldJoinThePumpBeforeReleasingTheWorker()
    {
        // Arrange
        CancellationFailureWorker? worker = null;
        var builder = SqlDatabaseEngine.CreateBuilder("sql-engine");
        builder.AddWorker(engine => worker = new CancellationFailureWorker(engine.Name + "/cancellation-failure"));
        var engine = builder.Build();
        worker.ShouldNotBeNull().Waiting.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // Act
        Task disposal = Task.Run(async () => await engine.DisposeAsync());
        bool cancelled = worker.Cancelled.Wait(TimeSpan.FromSeconds(10));
        bool completedBeforeTheWorkerStopped = disposal.IsCompleted;
        bool releasedBeforeTheWorkerStopped = worker.Released;
        worker.AllowStop.Set();
        var failure = await Should.ThrowAsync<AggregateException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert
        cancelled.ShouldBeTrue();
        completedBeforeTheWorkerStopped.ShouldBeFalse();
        releasedBeforeTheWorkerStopped.ShouldBeFalse();
        worker.Released.ShouldBeTrue();
        worker.StoppedBeforeRelease.ShouldBeTrue();
        failure.Flatten().InnerExceptions.ShouldContain(exception => exception.Message == "The cancellation callback failed.");
    }

    // A server whose stop awaits before it completes, which posts its continuation to the current
    // synchronization context unless the caller runs it on the thread pool.
    private sealed class YieldingServer : DatabaseServer
    {
        private int _stops;

        public YieldingServer(DatabaseEngine engine)
            : base(engine)
        {
        }

        public int Stops => Volatile.Read(ref _stops);

        public override IReadOnlyCollection<DatabaseServerSession> Sessions => [];

        protected override Task StartCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override async Task StopCoreAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            Interlocked.Increment(ref _stops);
        }
    }

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => throw new InvalidOperationException("Cleanup captured the caller's synchronization context.");
    }

    // A worker whose trigger wait registers a cancellation callback that throws, and whose pump
    // ends only once the test allows it, so the test can watch the engine wait for it.
    private sealed class CancellationFailureWorker : DatabaseEngineWorker
    {
        private bool _stopped;
        private bool _released;
        private bool _stoppedBeforeRelease;

        public CancellationFailureWorker(string name)
            : base(name, DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromHours(1))
        {
        }

        public ManualResetEventSlim Waiting { get; } = new();

        public ManualResetEventSlim Cancelled { get; } = new();

        public ManualResetEventSlim AllowStop { get; } = new();

        public bool Released => Volatile.Read(ref _released);

        public bool StoppedBeforeRelease => Volatile.Read(ref _stoppedBeforeRelease);

        protected override void RunIterationCore(CancellationToken cancellationToken)
        {
        }

        protected override void WaitForTrigger(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                Cancelled.Set();
                throw new InvalidOperationException("The cancellation callback failed.");
            });
            Waiting.Set();
            cancellationToken.WaitHandle.WaitOne();
            AllowStop.Wait();
            Volatile.Write(ref _stopped, true);
        }

        protected override ValueTask DisposeAsyncCore()
        {
            Volatile.Write(ref _stoppedBeforeRelease, Volatile.Read(ref _stopped));
            Volatile.Write(ref _released, true);
            return ValueTask.CompletedTask;
        }
    }
}
