using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL engine on the root <see cref="DatabaseEngine"/> base (concrete-types plan §6.4 and
/// §6.5, #1260): the typed database members over the base's public members, the base's guards and
/// their order, the disposal aggregate's shape when databases fail to close, and a database its
/// holder closed, which the engine forgets once the close ended so its next open reopens it, while
/// its workers skip it until then (owner decision 33).
/// Each test names the behavior it replaces.
/// </summary>
public sealed class SqlEngineContractTests
{
    /// <summary>
    /// The typed members hand out the <see cref="SqlDatabase"/> without a cast: the create, open and
    /// enumeration are <c>new</c> members over the base's, and the lookup is a typed overload, which
    /// an <c>out var</c> binds while an explicitly typed <see cref="DatabaseInstance"/> binds the
    /// base's.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: the typed members create, open, enumerate and look up SqlDatabase without a cast")]
    public async Task CreateDatabaseAsync_TypedMembers_ShouldHandOutTheSqlDatabase()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "typed" });

        // Act
        SqlDatabase created = await engine.CreateDatabaseAsync("orders", TestTimeout.Token());
        SqlDatabase collated = await engine.CreateDatabaseAsync("people", Collation.CaseInsensitive, TestTimeout.Token());
        SqlDatabase opened = await engine.OpenDatabaseAsync("orders", TestTimeout.Token());
        var names = new List<string>();
        await foreach (SqlDatabase database in engine.GetDatabasesAsync(TestTimeout.Token()))
        {
            names.Add(database.Name);
        }

        bool typedFound = engine.TryGetDatabase("orders", out var typed);
        SqlDatabase? lookedUp = typed;
        bool baseFound = engine.TryGetDatabase("orders", out DatabaseInstance? untyped);

        // Assert
        opened.ShouldBeSameAs(created);
        collated.Engine.ShouldBeSameAs(engine);
        created.SupportsSchemaProvisioning.ShouldBeTrue();
        names.ShouldBe(["orders", "people"], ignoreOrder: true);
        typedFound.ShouldBeTrue();
        lookedUp.ShouldBeSameAs(created);
        baseFound.ShouldBeTrue();
        untyped.ShouldBeSameAs(created);
    }

    /// <summary>
    /// The root engine base's guards (concrete-types plan §6.4, the engine's guards): every member
    /// checks the name, then disposal, then the token, and the enumeration checks disposal when it
    /// is called. Before the base, the SQL engine checked disposal first and the name after it, did
    /// not check the name in <c>TryGetDatabase</c>, observed no token on open or drop, and checked
    /// disposal at the enumeration's first <c>MoveNextAsync</c>. The collation overload of
    /// <c>CreateDatabaseAsync</c>, which repeats the base's checks rather than awaiting it, keeps
    /// the same order, with the null collation refused after the name and before disposal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: the base checks the name, then disposal, then the token, and the enumeration at its call")]
    public async Task Members_InvalidNameDisposedOrCanceled_ShouldCheckNameThenDisposalThenToken()
    {
        // Arrange
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "guards" });
        await engine.CreateDatabaseAsync("kept", TestTimeout.Token());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledOpen = await Should.ThrowAsync<OperationCanceledException>(async () => await engine.OpenDatabaseAsync("kept", canceled.Token));
        var canceledDrop = await Should.ThrowAsync<OperationCanceledException>(async () => await engine.DropDatabaseAsync("kept", canceled.Token));
        var unnamedLookup = Should.Throw<ArgumentException>(() => engine.TryGetDatabase(default, out _));
        bool keptAfterTheCanceledDrop = engine.TryGetDatabase("kept", out _);
        // The collation overload cannot await the base's member, so it repeats the base's checks:
        // a canceled token on a live engine is refused before anything is created.
        var canceledCollated = await Should.ThrowAsync<OperationCanceledException>(async () => await engine.CreateDatabaseAsync("collated", Collation.Binary, canceled.Token));
        bool collatedCreated = engine.TryGetDatabase("collated", out _);
        await engine.DisposeAsync();

        // Act
        var unnamedCreate = await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(default, canceled.Token));
        var unnamedOpen = await Should.ThrowAsync<ArgumentException>(async () => await engine.OpenDatabaseAsync(default, canceled.Token));
        var unnamedCollated = await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(default, Collation.Binary, canceled.Token));
        var nullCollation = await Should.ThrowAsync<ArgumentNullException>(async () => await engine.CreateDatabaseAsync("other", null!, canceled.Token));
        var disposedCreate = await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.CreateDatabaseAsync("other", canceled.Token));
        var disposedCollated = await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.CreateDatabaseAsync("other", Collation.Binary, canceled.Token));
        var disposedEnumeration = Should.Throw<ObjectDisposedException>(() => engine.GetDatabasesAsync());

        // Assert
        canceledOpen.CancellationToken.ShouldBe(canceled.Token);
        canceledDrop.CancellationToken.ShouldBe(canceled.Token);
        keptAfterTheCanceledDrop.ShouldBeTrue();
        canceledCollated.CancellationToken.ShouldBe(canceled.Token);
        collatedCreated.ShouldBeFalse();
        unnamedLookup.Message.ShouldStartWith("A database name is required.", Case.Sensitive);
        unnamedCreate.ParamName.ShouldBe("name");
        unnamedOpen.ParamName.ShouldBe("name");
        unnamedCollated.ParamName.ShouldBe("name");
        nullCollation.ParamName.ShouldBe("defaultCollation");
        disposedCreate.ShouldNotBeNull();
        disposedCollated.ShouldNotBeNull();
        disposedEnumeration.ShouldNotBeNull();
    }

    /// <summary>
    /// Databases that fail to close are one component of the engine's disposal aggregate
    /// (concrete-types plan §6.4): one failure is reported as itself, and two or more inside one
    /// nested aggregate, "One or more SQL databases failed to close.", under the base's "One or more
    /// components of engine '{name}' failed to close.". Before the root base the SQL engine's single
    /// aggregate, "Engine disposal encountered failures.", held each database's failure directly.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: databases that fail to close are one component of the engine's aggregate, several of them nested")]
    public async Task DisposeAsync_DatabasesFailToClose_ShouldReportThemAsOneComponent()
    {
        // Arrange: quiet workers, and databases holding a durable write; every journal flush of the
        // closes fails.
        var single = await CreateWithWritesAsync("single", databases: 1);
        var several = await CreateWithWritesAsync("several", databases: 2);

        // Act
        AggregateException singleFailure;
        AggregateException severalFailure;
        using (FaultInjectingJournalSqlStorageStrategy.FailJournalFlushes(100))
        {
            singleFailure = await Should.ThrowAsync<AggregateException>(async () => await single.DisposeAsync());
            severalFailure = await Should.ThrowAsync<AggregateException>(async () => await several.DisposeAsync());
        }

        // Assert
        singleFailure.Message.ShouldStartWith("One or more components of engine 'single' failed to close.", Case.Sensitive);
        singleFailure.InnerExceptions.ShouldHaveSingleItem().ShouldBeOfType<StorageOfflineException>();
        severalFailure.Message.ShouldStartWith("One or more components of engine 'several' failed to close.", Case.Sensitive);
        var databases = severalFailure.InnerExceptions.ShouldHaveSingleItem().ShouldBeOfType<AggregateException>();
        databases.Message.ShouldStartWith("One or more SQL databases failed to close.", Case.Sensitive);
        databases.InnerExceptions.Count.ShouldBe(2);
        databases.InnerExceptions.ShouldAllBe(failure => failure is StorageOfflineException);
        single.State.ShouldBe(EngineState.Disposed);
        several.State.ShouldBe(EngineState.Disposed);

        static async Task<SqlDatabaseEngine> CreateWithWritesAsync(string name, int databases)
        {
            var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
            {
                EngineName = name,
                StorageStrategy = new FaultInjectingJournalSqlStorageStrategy(durable: true),
                CheckpointInterval = TimeSpan.FromHours(1),
                PageWriteBackInterval = TimeSpan.FromHours(1),
                MaintenanceInterval = TimeSpan.FromHours(1),
            });

            for (int index = 0; index < databases; index++)
            {
                var database = await engine.CreateDatabaseAsync($"{name}-{index}", TestTimeout.Token());
                await using var session = await database.CreateSessionAsync(TestTimeout.Token());
                await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)", cancellationToken: TestTimeout.Token());
                await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)", cancellationToken: TestTimeout.Token());
            }

            return engine;
        }
    }

    /// <summary>
    /// A database a holder closed outside the engine is forgotten once its close ends, so the next
    /// open opens it again, a new instance with every row it held, in memory as on disk (owner
    /// decision 33, #1289; #1272's in-memory reopen): the engine's in-memory strategy keeps a
    /// database's two file sets for the engine's lifetime, and the open runs recovery over them as
    /// it does over files. A row of a transaction the close ended is not there. The reopened
    /// database takes writes, and a second close and open keeps them too. Until decision 33 the
    /// engine kept the closed instance registered until it was dropped, and an open handed it back,
    /// whose use threw <see cref="ObjectDisposedException"/>; before #1272 an in-memory reopen got
    /// empty storage and silently lost every row.
    /// </summary>
    /// <param name="onDisk">True for a file-backed engine; false for an in-memory one.</param>
    /// <param name="throughSession">Whether the database is closed through a session's database rather than directly.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Engine: a database closed outside the engine reopens with its rows, in memory and on disk")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OpenDatabaseAsync_DatabaseClosedOutsideTheEngine_ShouldReopenItWithItsRows(bool onDisk, bool throughSession)
    {
        // Arrange: a database with committed rows, and one more in an uncommitted transaction.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-reopen-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = SqlDatabaseEngine.Create(onDisk ? new() { RootPath = root } : new());
            var database = await engine.CreateDatabaseAsync("held", TestTimeout.Token());
            await using (var setup = await database.CreateSessionAsync(TestTimeout.Token()))
            {
                await setup.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)", cancellationToken: TestTimeout.Token());
                await setup.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2), (3)", cancellationToken: TestTimeout.Token());
            }

            var session = await database.CreateSessionAsync(TestTimeout.Token());
            _ = await session.BeginTransactionAsync(TestTimeout.Token());
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (99)", cancellationToken: TestTimeout.Token());

            // Act: close the database outside the engine, then open it again, write to it, and
            // close and open it once more.
            if (throughSession)
            {
                await session.Database.DisposeAsync();
            }
            else
            {
                await database.DisposeAsync();
            }

            bool foundAfterTheClose = engine.TryGetDatabase("held", out _);
            var reopened = await engine.OpenDatabaseAsync("held", TestTimeout.Token());
            long afterTheReopen = await CountAsync(reopened);
            await using (var writer = await reopened.CreateSessionAsync(TestTimeout.Token()))
            {
                await writer.ExecuteAsync("INSERT INTO t (id) VALUES (4)", cancellationToken: TestTimeout.Token());
            }

            await reopened.DisposeAsync();
            var again = await engine.OpenDatabaseAsync("held", TestTimeout.Token());

            // Assert
            foundAfterTheClose.ShouldBeFalse();
            reopened.ShouldNotBeSameAs(database);
            afterTheReopen.ShouldBe(3);
            again.ShouldNotBeSameAs(reopened);
            (await CountAsync(again)).ShouldBe(4);
            engine.IsOpen(again).ShouldBeTrue();
            engine.State.ShouldBe(EngineState.Running);
            await session.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// While a holder's close of a database runs, the engine still tracks the closing instance and
    /// nothing races the close for its files (owner decision 33, #1289): the lookup does not report
    /// it, a create of its name is refused as existing, every worker's pass skips it (a checkpoint
    /// of both file sets is due, and none runs: the closed-database guards hold for the window
    /// between the close and the engine forgetting it), and an open waits for the close to end.
    /// Then the engine forgets it, and the open opens it again with its rows. The close is held in
    /// its shutdown checkpoint by a data fsync that does not answer.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: while a holder closes a database, workers skip it and an open waits, then reopens it with its rows")]
    public async Task OpenDatabaseAsync_WhileAHolderClosesTheDatabase_ShouldWaitAndThenReopenIt()
    {
        // Arrange: a database with rows and a checkpoint due, whose data fsync will stall.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create(QuietOptions(strategy));
        var database = await CreateWithRowsAsync(engine);
        database.DataStorage.CheckpointJournalSize = 1;
        database.CatalogStorage.CheckpointJournalSize = 1;
        var faults = strategy.Faults("held");
        faults.StallDataFlushes();
        using var released = new Release(faults.ReleaseDataFlushes);

        // Act: the holder's close stalls in its shutdown checkpoint; meanwhile every worker runs a
        // pass, the name is looked up and created, and an open starts.
        var close = Task.Run(async () => await database.DisposeAsync());
        bool stalled = await Eventually(() => faults.StalledDataFlushes == 1);
        bool trackedWhileClosing = Array.IndexOf(engine.GetInstanceSnapshot(), database) >= 0;
        var passes = engine.Workers.Select(worker => worker.RunIteration(TestTimeout.Token())).ToArray();
        bool foundWhileClosing = engine.TryGetDatabase("held", out _);
        var create = await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("held", TestTimeout.Token()));
        var open = engine.OpenDatabaseAsync("held", TestTimeout.Token(30)).AsTask();
        bool openWaited = !await CompletesWithin(open, TimeSpan.FromMilliseconds(200));
        faults.ReleaseDataFlushes();
        await close.WaitAsync(TestTimeout.Token(30));
        var reopened = await open.WaitAsync(TestTimeout.Token(30));

        // Assert
        stalled.ShouldBeTrue();
        trackedWhileClosing.ShouldBeTrue();
        passes.ShouldAllBe(passed => passed);
        engine.Workers.ShouldAllBe(worker => worker.FailureCount == 0 && worker.Fault == null);
        foundWhileClosing.ShouldBeFalse();
        create.Message.ShouldBe("A database with name 'held' already exists.");
        openWaited.ShouldBeTrue();
        reopened.ShouldNotBeSameAs(database);
        engine.IsOpen(database).ShouldBeFalse();
        engine.IsOpen(reopened).ShouldBeTrue();
        (await CountAsync(reopened)).ShouldBe(2);
        engine.State.ShouldBe(EngineState.Running);
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    /// <summary>
    /// A drop during a holder's close of the database waits for the close to end before it drops
    /// the files: the engine lets the database go and disposes it, and that disposal waits for the
    /// close a holder started, whose end does not wait for the engine's lock (owner decision 33,
    /// #1289). Nothing deadlocks; the database is gone afterwards and its name can be created again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: a drop during a holder's close waits for the close, then drops the database")]
    public async Task DropDatabaseAsync_WhileAHolderClosesTheDatabase_ShouldWaitForTheCloseThenDropIt()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create(QuietOptions(strategy));
        var database = await CreateWithRowsAsync(engine);
        var faults = strategy.Faults("held");
        faults.StallDataFlushes();
        using var released = new Release(faults.ReleaseDataFlushes);

        // Act: the holder's close stalls in its shutdown checkpoint, and a drop starts.
        var close = Task.Run(async () => await database.DisposeAsync());
        bool stalled = await Eventually(() => faults.StalledDataFlushes == 1);
        var drop = Task.Run(async () => await engine.DropDatabaseAsync("held", TestTimeout.Token(30)));
        bool dropWaited = !await CompletesWithin(drop, TimeSpan.FromMilliseconds(200));
        bool filesWhileClosing = strategy.StorageExists("held");
        faults.ReleaseDataFlushes();
        await close.WaitAsync(TestTimeout.Token(30));
        await drop.WaitAsync(TestTimeout.Token(30));

        // Assert
        stalled.ShouldBeTrue();
        dropWaited.ShouldBeTrue();
        filesWhileClosing.ShouldBeTrue();
        strategy.StorageExists("held").ShouldBeFalse();
        await Should.ThrowAsync<DatabaseNotFoundException>(async () => await engine.OpenDatabaseAsync("held", TestTimeout.Token()));
        var recreated = await engine.CreateDatabaseAsync("held", TestTimeout.Token());
        engine.IsOpen(recreated).ShouldBeTrue();
        engine.State.ShouldBe(EngineState.Running);
    }

    // Quiet workers: the tests run the passes they need themselves.
    private static SqlDatabaseEngineOptions QuietOptions(FaultInjectingJournalSqlStorageStrategy strategy) => new()
    {
        EngineName = "closing",
        StorageStrategy = strategy,
        CheckpointInterval = TimeSpan.FromHours(1),
        PageWriteBackInterval = TimeSpan.FromHours(1),
        MaintenanceInterval = TimeSpan.FromHours(1),
    };

    // A database "held" with a table of two rows.
    private static async Task<SqlDatabase> CreateWithRowsAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("held", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)", cancellationToken: TestTimeout.Token());
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2)", cancellationToken: TestTimeout.Token());
        return database;
    }

    private static async Task<long> CountAsync(SqlDatabase database)
    {
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        var result = await session.ExecuteAsync("SELECT COUNT(*) FROM t", cancellationToken: TestTimeout.Token());
        var set = result.ShouldBeAssignableTo<QueryResultSet>().ShouldNotBeNull();
        await using (set)
        {
            await foreach (var row in set.GetRowsAsync())
            {
                return Convert.ToInt64(row.GetValue(0));
            }
        }

        throw new InvalidOperationException("The count returned no row.");
    }

    // Polls a condition another thread makes true, for at most ten seconds.
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
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
