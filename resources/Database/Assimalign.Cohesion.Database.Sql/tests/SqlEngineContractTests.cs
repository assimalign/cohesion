using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL engine on the root <see cref="DatabaseEngine"/> base (concrete-types plan §6.4 and
/// §6.5, #1260): the typed database members over the base's public members, the base's guards and
/// their order, the disposal aggregate's shape when databases fail to close, and a database its
/// holder closed, which the engine keeps registered to refuse its reopen while its workers skip it.
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
        created.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
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
    /// A database disposed outside the engine stays registered until it is dropped (an open hands
    /// back the closed instance, whose use throws <see cref="ObjectDisposedException"/>), and the
    /// engine's workers skip it: every worker's pass over it succeeds and records nothing, and the
    /// engine stays <see cref="EngineState.Running"/>. Dropping it removes it, and a database of that
    /// name can be created again. Before the fix the engine's open-database test read only its
    /// registration, so the version-purge worker failed on the closed one's disposed transaction
    /// manager every pass, the checkpointer on its disposed journal once it was due, and the engine
    /// stayed <see cref="EngineState.Faulted"/> for good.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: a database disposed outside the engine is skipped by every worker until it is dropped")]
    public async Task DisposeAsync_DatabaseOutsideTheEngine_ShouldBeSkippedByEveryWorkerUntilDropped()
    {
        // Arrange: a database with committed rows and dirty pages, disposed by its holder.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "closed",
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("held", TestTimeout.Token());
        await using (var session = await database.CreateSessionAsync(TestTimeout.Token()))
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL)", cancellationToken: TestTimeout.Token());
            await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)", cancellationToken: TestTimeout.Token());
        }

        database.DataStorage.CheckpointJournalSize = 1;
        database.CatalogStorage.CheckpointJournalSize = 1;

        // Act: the holder closes it, and every worker runs passes over it.
        await database.DisposeAsync();
        var passes = new List<bool>();
        for (int round = 0; round < 3; round++)
        {
            foreach (var worker in engine.Workers)
            {
                passes.Add(worker.RunIteration(TestTimeout.Token()));
            }
        }

        bool registered = engine.TryGetDatabase("held", out var stillRegistered);
        var reopen = await Should.ThrowAsync<ObjectDisposedException>(async () => await (await engine.OpenDatabaseAsync("held", TestTimeout.Token())).CreateSessionAsync(TestTimeout.Token()));
        await engine.DropDatabaseAsync("held", TestTimeout.Token());
        var recreated = await engine.CreateDatabaseAsync("held", TestTimeout.Token());

        // Assert
        passes.ShouldAllBe(passed => passed);
        engine.Workers.ShouldAllBe(worker => worker.FailureCount == 0 && worker.Fault == null);
        engine.State.ShouldBe(EngineState.Running);
        engine.OfflineDatabases.ShouldBeEmpty();
        registered.ShouldBeTrue();
        stillRegistered.ShouldBeSameAs(database);
        database.IsClosed.ShouldBeTrue();
        engine.IsOpen(database).ShouldBeFalse();
        reopen.ShouldNotBeNull();
        recreated.ShouldNotBeSameAs(database);
        engine.IsOpen(recreated).ShouldBeTrue();
    }
}
