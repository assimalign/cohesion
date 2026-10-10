using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// The graph engine builder's B3 seams, the SQL builder's shape (the engine extensibility
/// design's §3.3 and §3.11): the databases it declares, opened or created before the build returns
/// and owned by the built engine; the options snapshot; the server the model creates from options,
/// and the server's own copy of them. No test here references the hosting layer.
/// </summary>
public sealed class GraphEngineDeclarationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "cohesion-graph-declarations",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: Build creates a declared database that does not exist")]
    public async Task BuildAsync_DeclaredDatabaseMissing_ShouldCreateIt()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("create-path");
        builder.AddDatabase("sales").AddDatabase("audit");

        // Act
        await using var engine = await builder.BuildAsync(TestTimeout.Token());

        // Assert: both are open when the build returns, in declaration order.
        engine.TryGetDatabase("sales", out GraphDatabase? _).ShouldBeTrue();
        engine.TryGetDatabase("audit", out GraphDatabase? _).ShouldBeTrue();
        engine.DeclaredDatabases.ShouldBe(new[] { new DatabaseName("sales"), new DatabaseName("audit") });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: Build opens a declared database that exists, with its nodes")]
    public async Task BuildAsync_DeclaredDatabaseExists_ShouldOpenItWithItsData()
    {
        // Arrange: a first build creates the database, and its nodes gets one.
        await using (var first = await CreateBuilder("open-path").AddDatabase("sales").BuildAsync(TestTimeout.Token()))
        {
            first.TryGetDatabase("sales", out GraphDatabase? created).ShouldBeTrue();
            await using var session = await created!.CreateSessionAsync();
            await session.ExecuteAsync("INSERT (:Kept {name: 'kept'})", cancellationToken: TestTimeout.Token());
        }

        // Act: a second engine over the same files declares the same database.
        await using var second = await CreateBuilder("open-path").AddDatabase("SALES").BuildAsync(TestTimeout.Token());

        // Assert: opened, not created again.
        second.TryGetDatabase("sales", out GraphDatabase? opened).ShouldBeTrue();
        await using var reader = await opened!.CreateSessionAsync();
        await using var result = (QueryResultSet)await reader.ExecuteAsync("MATCH (n:Kept) RETURN n.name", cancellationToken: TestTimeout.Token());
        var names = new List<object?>();
        await foreach (var row in result.GetRowsAsync(TestTimeout.Token()))
        {
            names.Add(row.GetValue(0));
        }

        names.ShouldBe(new object?[] { "kept" });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: the engine refuses to drop a declared database, and drops any other")]
    public async Task DropDatabaseAsync_DeclaredDatabase_ShouldBeRefused()
    {
        // Arrange: the synchronous bridge builds the same way.
        await using var engine = GraphDatabaseEngine.CreateBuilder("owner").AddDatabase("sales").Build();
        await engine.CreateDatabaseAsync("adhoc");

        // Act
        var refusal = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await engine.DropDatabaseAsync("SALES"));
        await engine.DropDatabaseAsync("adhoc");

        // Assert: the declaration owns the database (owner decision 56), the refusal names it as
        // declared and says how to drop it, and the database stays open.
        refusal.ObjectName.ShouldBe("sales");
        refusal.OwningSchema.ShouldBe("sales");
        refusal.Operation.ShouldBe("DROP DATABASE");
        refusal.Message.ShouldBe(
            "Graph engine 'owner' declares database 'sales' (GraphDatabaseEngineBuilder.AddDatabase), so DROP DATABASE is " +
            "refused. Remove the declaration from the engine builder and rebuild the engine before dropping it.");
        engine.TryGetDatabase("sales", out GraphDatabase? _).ShouldBeTrue();
        engine.TryGetDatabase("adhoc", out GraphDatabase? _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: a database is declared once per engine, and none after the build began")]
    public async Task AddDatabase_Declarations_ShouldBeCheckedAtTheCall()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("declarations");
        builder.AddDatabase("sales");

        // Act
        var duplicate = Should.Throw<InvalidOperationException>(() => builder.AddDatabase("SALES"));
        var blank = Should.Throw<ArgumentException>(() => builder.AddDatabase(" "));
        await using var engine = await builder.BuildAsync(TestTimeout.Token());
        var frozen = Should.Throw<InvalidOperationException>(() => builder.AddDatabase("late"));

        // Assert: refused case-insensitively, as database names compare.
        duplicate.Message.ShouldBe("Graph engine 'declarations' already declares database 'sales'.");
        blank.ParamName.ShouldBe("name");
        frozen.Message.ShouldBe("Engine 'declarations': composition is frozen after a build attempt.");
        engine.DeclaredDatabases.ShouldHaveSingleItem().ShouldBe(new DatabaseName("sales"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: a build canceled while it opens its databases disposes the engine, its workers and servers")]
    public async Task BuildAsync_CanceledWhileOpening_ShouldDisposeTheEngine()
    {
        // Arrange: the worker factory runs after the engine exists, and cancels before the
        // declared database is opened.
        using var cancel = new CancellationTokenSource();
        GraphDatabaseEngine? product = null;
        RecordingWorker? worker = null;
        RecordingServer? server = null;
        var builder = CreateBuilder("canceled-late").AddDatabase("sales");
        builder.AddWorker(engine =>
        {
            product = engine;
            cancel.Cancel();
            return worker = new RecordingWorker(engine);
        });
        builder.AddServer(engine => server = new RecordingServer(engine));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await builder.BuildAsync(cancel.Token));

        // Assert: nothing the build made outlives it, and the declared database was not created.
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
        Directory.Exists(Path.Combine(_rootPath, "sales")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: a build canceled before it began creates nothing")]
    public async Task BuildAsync_Canceled_ShouldCreateNothing()
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        GraphDatabaseEngine? product = null;
        var builder = CreateBuilder("canceled").AddDatabase("sales");
        builder.AddWorker(engine => new RecordingWorker(product = engine));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await builder.BuildAsync(canceled.Token));

        // Assert
        product.ShouldBeNull();
        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: a change to the options after Build, or after Create, does not reach the engine")]
    public async Task Options_ChangedAfterBuild_ShouldNotReachTheEngine()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("snapshot");
        builder.Options.PageWriteBackBatchSize = 64;
        builder.Options.MaintenanceInterval = TimeSpan.FromMinutes(7);
        var options = new GraphDatabaseEngineOptions { PageWriteBackBatchSize = 64 };

        // Act
        await using var built = await builder.BuildAsync(TestTimeout.Token());
        await using var created = GraphDatabaseEngine.Create("created", options);
        builder.Options.PageWriteBackBatchSize = 1;
        builder.Options.MaintenanceInterval = TimeSpan.FromSeconds(1);
        options.PageWriteBackBatchSize = 1;

        // Assert: each engine keeps its own copy (the write-back worker reads the batch size on
        // every pass, so the caller's object used to reach a running engine).
        built.EngineOptions.ShouldNotBeSameAs(builder.Options);
        built.EngineOptions.PageWriteBackBatchSize.ShouldBe(64);
        built.EngineOptions.MaintenanceInterval.ShouldBe(TimeSpan.FromMinutes(7));
        created.EngineOptions.ShouldNotBeSameAs(options);
        created.EngineOptions.PageWriteBackBatchSize.ShouldBe(64);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: the options copy keeps every option")]
    public async Task Options_Snapshot_ShouldCopyEveryOption()
    {
        // Arrange: every option away from its default. The strategy is the one the engine uses, so
        // the root path is set beside it only to show the copy keeps it.
        var clock = new Assimalign.Cohesion.Database.Tests.ManualTimeProvider();
        var strategy = new FaultInjectingJournalStorageStrategy();
        string rootPath = Path.Combine(_rootPath, "unused-root");
        var options = new GraphDatabaseEngineOptions
        {
            RootPath = rootPath,
            StorageStrategy = strategy,
            Durability = Assimalign.Cohesion.Database.Storage.StorageCommitDurability.None,
            GroupCommitWindow = TimeSpan.FromMilliseconds(7),
            CheckpointInterval = TimeSpan.FromMinutes(9),
            CheckpointJournalSize = 3 * 1024 * 1024,
            WorkerFailureWindow = TimeSpan.FromSeconds(42),
            WorkerFailureMinimumPasses = 5,
            JournalSizeLimit = 12 * 1024 * 1024,
            BufferPoolCapacity = 2 * 1024 * 1024,
            PageWriteBackInterval = TimeSpan.FromSeconds(3),
            PageWriteBackBatchSize = 33,
            MaintenanceInterval = TimeSpan.FromMinutes(4),
            DeferredUndoRetryDelay = TimeSpan.FromMilliseconds(250),
            TimeProvider = clock,
        };

        // Act
        await using var engine = GraphDatabaseEngine.Create("every-option", options);
        var copy = engine.EngineOptions;

        // Assert
        engine.Name.ShouldBe("every-option");
        copy.ShouldNotBeSameAs(options);
        copy.Durability.ShouldBe(options.Durability);
        copy.GroupCommitWindow.ShouldBe(options.GroupCommitWindow);
        copy.CheckpointInterval.ShouldBe(options.CheckpointInterval);
        copy.CheckpointJournalSize.ShouldBe(options.CheckpointJournalSize);
        copy.WorkerFailureWindow.ShouldBe(options.WorkerFailureWindow);
        copy.WorkerFailureMinimumPasses.ShouldBe(options.WorkerFailureMinimumPasses);
        copy.JournalSizeLimit.ShouldBe(options.JournalSizeLimit);
        copy.BufferPoolCapacity.ShouldBe(options.BufferPoolCapacity);
        copy.PageWriteBackInterval.ShouldBe(options.PageWriteBackInterval);
        copy.PageWriteBackBatchSize.ShouldBe(options.PageWriteBackBatchSize);
        copy.MaintenanceInterval.ShouldBe(options.MaintenanceInterval);
        copy.DeferredUndoRetryDelay.ShouldBe(options.DeferredUndoRetryDelay);
        copy.TimeProvider.ShouldBeSameAs(clock);
        copy.RootPath.ShouldNotBeNull();
        copy.RootPath.ShouldBe(options.RootPath);
        copy.StorageStrategy.ShouldBeSameAs(strategy);
        typeof(GraphDatabaseEngineOptions).GetProperties().Length.ShouldBe(12);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: AddServer with options creates a graph server over the engine, and a server it cannot create fails the build")]
    public async Task AddServer_WithOptions_ShouldCreateTheModelsServer()
    {
        // Arrange
        var listener = new InMemoryConnectionListener();
        var builder = GraphDatabaseEngine.CreateBuilder("served");
        builder.AddServer(server => server.Listener = listener);
        var refused = GraphDatabaseEngine.CreateBuilder("unserved");
        GraphDatabaseEngine? product = null;
        refused.AddWorker(engine => new RecordingWorker(product = engine));
        refused.AddServer(server => server.MaxSessions = 4);

        // Act
        await using var engine = await builder.BuildAsync(TestTimeout.Token());
        var failure = await Should.ThrowAsync<ArgumentException>(async () => await refused.BuildAsync(TestTimeout.Token()));

        // Assert
        var server = engine.Servers.ShouldHaveSingleItem().ShouldBeOfType<GraphDatabaseServer>();
        server.Engine.ShouldBeSameAs(engine);
        failure.Message.ShouldStartWith("A connection listener is required.", Case.Sensitive);
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Declarations: a change to the server options after Build or Create does not reach the server")]
    public async Task ServerOptions_ChangedAfterBuild_ShouldNotReachTheServer()
    {
        // Arrange
        GraphDatabaseServerOptions? captured = null;
        var builder = GraphDatabaseEngine.CreateBuilder("served-options");
        builder.AddServer(server =>
        {
            server.Listener = new InMemoryConnectionListener();
            server.MaxSessions = 8;
            captured = server;
        });
        var direct = new GraphDatabaseServerOptions { Listener = new InMemoryConnectionListener(), MaxSessions = 3 };

        // Act
        await using var engine = await builder.BuildAsync(TestTimeout.Token());
        await using var other = GraphDatabaseEngine.Create("direct", new GraphDatabaseEngineOptions());
        var created = GraphDatabaseServer.Create(other, direct);
        captured!.MaxSessions = 0;
        direct.MaxSessions = 0;

        // Assert: each server keeps its own checked copy (its sessions read the limits live).
        var server = engine.Servers.ShouldHaveSingleItem().ShouldBeOfType<GraphDatabaseServer>();
        server.ServerOptions.ShouldNotBeSameAs(captured);
        server.ServerOptions.MaxSessions.ShouldBe(8);
        created.ServerOptions.ShouldNotBeSameAs(direct);
        created.ServerOptions.MaxSessions.ShouldBe(3);
        await created.DisposeAsync();
    }

    private GraphDatabaseEngineBuilder CreateBuilder(string name)
    {
        var builder = GraphDatabaseEngine.CreateBuilder(name);
        builder.Options.RootPath = _rootPath;
        return builder;
    }
}
