using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// The document engine builder's B3 seams, the SQL builder's shape (the engine extensibility
/// design's §3.3 and §3.11): the databases it declares, opened or created before the build returns
/// and owned by the built engine; the options snapshot. The model has no server of its own; a
/// server factory still composes one. No test here references the hosting layer.
/// </summary>
public sealed class DocumentEngineDeclarationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "cohesion-document-declarations",
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

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: Build creates a declared database that does not exist")]
    public async Task BuildAsync_DeclaredDatabaseMissing_ShouldCreateIt()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("create-path");
        builder.AddDatabase("sales").AddDatabase("audit");

        // Act
        await using var engine = await builder.BuildAsync(CancellationToken.None);

        // Assert: both are open when the build returns, in declaration order.
        engine.TryGetDatabase("sales", out DocumentDatabase? _).ShouldBeTrue();
        engine.TryGetDatabase("audit", out DocumentDatabase? _).ShouldBeTrue();
        engine.DeclaredDatabases.ShouldBe(new[] { new DatabaseName("sales"), new DatabaseName("audit") });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: Build opens a declared database that exists, with its collections")]
    public async Task BuildAsync_DeclaredDatabaseExists_ShouldOpenItWithItsData()
    {
        // Arrange: a first build creates the database, and its collections gets one.
        await using (var first = await CreateBuilder("open-path").AddDatabase("sales").BuildAsync(CancellationToken.None))
        {
            first.TryGetDatabase("sales", out DocumentDatabase? created).ShouldBeTrue();
            await using var session = await created!.CreateSessionAsync();
            await session.CreateCollectionAsync("kept");
        }

        // Act: a second engine over the same files declares the same database.
        await using var second = await CreateBuilder("open-path").AddDatabase("SALES").BuildAsync(CancellationToken.None);

        // Assert: opened, not created again.
        second.TryGetDatabase("sales", out DocumentDatabase? opened).ShouldBeTrue();
        await using var reader = await opened!.CreateSessionAsync();
        (await reader.GetCollectionAsync("kept")).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: the engine refuses to drop a declared database, and drops any other")]
    public async Task DropDatabaseAsync_DeclaredDatabase_ShouldBeRefused()
    {
        // Arrange: the synchronous bridge builds the same way.
        await using var engine = DocumentDatabaseEngine.CreateBuilder("owner").AddDatabase("sales").Build();
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
            "Document engine 'owner' declares database 'sales' (DocumentDatabaseEngineBuilder.AddDatabase), so DROP DATABASE is " +
            "refused. Remove the declaration from the engine builder and rebuild the engine before dropping it.");
        engine.TryGetDatabase("sales", out DocumentDatabase? _).ShouldBeTrue();
        engine.TryGetDatabase("adhoc", out DocumentDatabase? _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: a database is declared once per engine, and none after the build began")]
    public async Task AddDatabase_Declarations_ShouldBeCheckedAtTheCall()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("declarations");
        builder.AddDatabase("sales");

        // Act
        var duplicate = Should.Throw<InvalidOperationException>(() => builder.AddDatabase("SALES"));
        var blank = Should.Throw<ArgumentException>(() => builder.AddDatabase(" "));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var frozen = Should.Throw<InvalidOperationException>(() => builder.AddDatabase("late"));

        // Assert: refused case-insensitively, as database names compare.
        duplicate.Message.ShouldBe("Document engine 'declarations' already declares database 'sales'.");
        blank.ParamName.ShouldBe("name");
        frozen.Message.ShouldBe("Engine 'declarations': composition is frozen after a build attempt.");
        engine.DeclaredDatabases.ShouldHaveSingleItem().ShouldBe(new DatabaseName("sales"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: a build canceled while it opens its databases disposes the engine, its workers and servers")]
    public async Task BuildAsync_CanceledWhileOpening_ShouldDisposeTheEngine()
    {
        // Arrange: the worker factory runs after the engine exists, and cancels before the
        // declared database is opened.
        using var cancel = new CancellationTokenSource();
        DocumentDatabaseEngine? product = null;
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

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: a build canceled before it began creates nothing")]
    public async Task BuildAsync_Canceled_ShouldCreateNothing()
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        DocumentDatabaseEngine? product = null;
        var builder = CreateBuilder("canceled").AddDatabase("sales");
        builder.AddWorker(engine => new RecordingWorker(product = engine));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await builder.BuildAsync(canceled.Token));

        // Assert
        product.ShouldBeNull();
        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: a change to the options after Build, or after Create, does not reach the engine")]
    public async Task Options_ChangedAfterBuild_ShouldNotReachTheEngine()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("snapshot");
        builder.Options.PageWriteBackBatchSize = 64;
        builder.Options.MaintenanceInterval = TimeSpan.FromMinutes(7);
        var options = new DocumentDatabaseEngineOptions { PageWriteBackBatchSize = 64 };

        // Act
        await using var built = await builder.BuildAsync(CancellationToken.None);
        await using var created = DocumentDatabaseEngine.Create("created", options);
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

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Declarations: the options copy keeps every option")]
    public async Task Options_Snapshot_ShouldCopyEveryOption()
    {
        // Arrange: every option away from its default. The strategy is the one the engine uses, so
        // the root path is set beside it only to show the copy keeps it.
        var clock = new Assimalign.Cohesion.Database.Tests.ManualTimeProvider();
        var strategy = new FaultInjectingJournalStorageStrategy();
        string rootPath = Path.Combine(_rootPath, "unused-root");
        var options = new DocumentDatabaseEngineOptions
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
        await using var engine = DocumentDatabaseEngine.Create("every-option", options);
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
        typeof(DocumentDatabaseEngineOptions).GetProperties().Length.ShouldBe(12);
    }

    private DocumentDatabaseEngineBuilder CreateBuilder(string name)
    {
        var builder = DocumentDatabaseEngine.CreateBuilder(name);
        builder.Options.RootPath = _rootPath;
        return builder;
    }
}
