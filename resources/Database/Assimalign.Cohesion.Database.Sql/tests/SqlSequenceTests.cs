using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SqlIdentityConcurrency")]
public sealed class SqlSequenceTests
{
    private readonly ITestOutputHelper _output;

    public SqlSequenceTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: resolved defaults follow the integer type and direction")]
    [InlineData(DatabaseType.Int16, 1)][InlineData(DatabaseType.Int16, -1)]
    [InlineData(DatabaseType.Int32, 1)][InlineData(DatabaseType.Int32, -1)]
    [InlineData(DatabaseType.Int64, 1)][InlineData(DatabaseType.Int64, -1)]
    public void Options_DirectionalDefaults_ShouldMatchType(DatabaseType type, long increment)
    {
        // Arrange / Act
        var options = new SqlSequenceOptions(type, increment: increment);
        var (minimum, maximum) = SqlSequenceOptions.IntegerBounds(type);

        // Assert
        options.Minimum.ShouldBe(increment > 0 ? 1 : minimum);
        options.Maximum.ShouldBe(increment > 0 ? maximum : -1);
        options.Start.ShouldBe(increment > 0 ? options.Minimum : options.Maximum);
        options.Cache.ShouldBe(1);
        options.Cycle.ShouldBeFalse();
        new SqlSequenceOptions().Type.ShouldBe(DatabaseType.Int64);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: every invalid resolved option has a catalog diagnostic")]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    [InlineData(6)][InlineData(7)][InlineData(8)][InlineData(9)][InlineData(10)][InlineData(11)]
    [InlineData(12)][InlineData(13)][InlineData(14)][InlineData(15)]
    public void Options_InvalidOption_ShouldRefuse(int scenario)
    {
        // Arrange / Act
        var failure = Should.Throw<SqlCatalogException>(() =>
        {
            _ = scenario switch
            {
                0 => new SqlSequenceOptions(DatabaseType.Int8),
                1 => new SqlSequenceOptions(DatabaseType.String),
                2 => new SqlSequenceOptions(increment: 0),
                3 => new SqlSequenceOptions(minimum: 2, maximum: 2),
                4 => new SqlSequenceOptions(minimum: 3, maximum: 2),
                5 => new SqlSequenceOptions(DatabaseType.Int16, minimum: short.MinValue - 1L),
                6 => new SqlSequenceOptions(DatabaseType.Int16, minimum: short.MaxValue + 1L),
                7 => new SqlSequenceOptions(DatabaseType.Int16, maximum: short.MinValue - 1L),
                8 => new SqlSequenceOptions(DatabaseType.Int16, maximum: short.MaxValue + 1L),
                9 => new SqlSequenceOptions(start: 0),
                10 => new SqlSequenceOptions(start: 11, maximum: 10),
                11 => new SqlSequenceOptions(cache: 0),
                12 => new SqlSequenceOptions(cache: -1),
                13 => new SqlSequenceOptions(DatabaseType.Int32, minimum: int.MinValue - 1L),
                14 => new SqlSequenceOptions(DatabaseType.Int32, maximum: int.MaxValue + 1L),
                _ => new SqlSequenceOptions((DatabaseType)255),
            };
        });

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03069");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence: restart accepts both bounds and refuses values outside either bound")]
    public void Options_RestartBounds_ShouldValidateBothEnds()
    {
        // Arrange
        var options = new SqlSequenceOptions(minimum: -3, maximum: 3);

        // Act / Assert
        options.ValidateRestart(-3);
        options.ValidateRestart(3);
        Should.Throw<SqlCatalogException>(() => options.ValidateRestart(-4)).Diagnostics.Single().Code.ShouldBe("CS03069");
        Should.Throw<SqlCatalogException>(() => options.ValidateRestart(4)).Diagnostics.Single().Code.ShouldBe("CS03069");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: cached allocation adds no bytes beyond T1 identity facet lookup")]
    [InlineData(false)][InlineData(true)]
    public async Task Allocation_CachedValues_ShouldPreserveT1AllocationBudget(bool identity)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("allocation-bytes", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new());
        var table = await catalog.CreateTableAsync("dbo", "ids", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int64),
            identity: new SqlCatalogIdentity(false))]);
        long Allocate() => identity ? catalog.AllocateIdentity(table) : catalog.AllocateSequence(sequence);
        Allocate().ShouldBe(1);
        // T1 already enumerates the readonly column collection with SingleOrDefault.
        // Measure that unchanged lookup on this runtime, independently of allocation.
        static SqlCatalogColumn? FindIdentity(SqlCatalogTable description)
            => description.Columns.SingleOrDefault(column => column.Identity is not null);
        GC.KeepAlive(FindIdentity(table));
        long lookupBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20; index++) { GC.KeepAlive(FindIdentity(table)); }
        long lookupBytes = GC.GetAllocatedBytesForCurrentThread() - lookupBefore;

        // Act: use only values already covered by the durable reservation.
        long total = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20; index++) { total += Allocate(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        total.ShouldBe(230);
        allocated.ShouldBe(identity ? lookupBytes : 0);
        _output.WriteLine($"identity={identity}: cached bytes={allocated}, T1 facet lookup bytes={lookupBytes}, values=20");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: exhaustion and CYCLE use each integer type's exact limits")]
    [InlineData(DatabaseType.Int16, 1, false)][InlineData(DatabaseType.Int16, -1, false)]
    [InlineData(DatabaseType.Int32, 1, false)][InlineData(DatabaseType.Int32, -1, false)]
    [InlineData(DatabaseType.Int64, 1, false)][InlineData(DatabaseType.Int64, -1, false)]
    [InlineData(DatabaseType.Int16, 1, true)][InlineData(DatabaseType.Int16, -1, true)]
    [InlineData(DatabaseType.Int32, 1, true)][InlineData(DatabaseType.Int32, -1, true)]
    [InlineData(DatabaseType.Int64, 1, true)][InlineData(DatabaseType.Int64, -1, true)]
    public async Task AllocateSequence_TypeBoundary_ShouldExhaustOrWrap(DatabaseType type, long increment, bool cycle)
    {
        // Arrange
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("sequence-bounds", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var catalog = database.Catalog;
        var (minimum, maximum) = SqlSequenceOptions.IntegerBounds(type);
        long start = increment > 0 ? maximum - 1 : minimum + 1;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(type, start, increment, minimum, maximum, cycle: cycle));
        int reservations = 0;
        catalog.ReservationCommitted = () => reservations++;

        // Act / Assert
        catalog.AllocateSequence(sequence).ShouldBe(start);
        catalog.AllocateSequence(sequence).ShouldBe(increment > 0 ? maximum : minimum);
        var image = strategy.CaptureDurableImages();
        await using var recovered = SqlDatabaseEngine.Create("sequence-boundary-recovery", new() { StorageStrategy = image });
        var restored = await recovered.OpenDatabaseAsync("db", CancellationToken.None);
        var restoredSequence = restored.Catalog.FindSequence("dbo", "s").ShouldNotBeNull();
        restoredSequence.Options.Type.ShouldBe(type);
        restoredSequence.Options.Start.ShouldBe(start);
        restoredSequence.Options.Increment.ShouldBe(increment);
        restoredSequence.Options.Minimum.ShouldBe(minimum);
        restoredSequence.Options.Maximum.ShouldBe(maximum);
        restoredSequence.Options.Cycle.ShouldBe(cycle);
        if (cycle)
        {
            restored.Catalog.AllocateSequence(restoredSequence).ShouldBe(increment > 0 ? minimum : maximum);
            catalog.AllocateSequence(sequence).ShouldBe(increment > 0 ? minimum : maximum);
            catalog.AllocateSequence(sequence).ShouldBe(increment > 0 ? minimum + 1 : maximum - 1);
            reservations.ShouldBe(2);
        }
        else
        {
            Should.Throw<SqlCatalogException>(() => restored.Catalog.AllocateSequence(restoredSequence)).Diagnostics.Single().Code.ShouldBe("CS02002");
            for (int attempt = 0; attempt < 2; attempt++)
            { Should.Throw<SqlCatalogException>(() => catalog.AllocateSequence(sequence)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02002"); }
            reservations.ShouldBe(1);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: extreme signed increments do not overflow")]
    [InlineData(long.MinValue)][InlineData(long.MaxValue)]
    public async Task AllocateSequence_ExtremeIncrement_ShouldUseWideArithmetic(long increment)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-wide", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var sequence = await database.Catalog.CreateSequenceAsync("dbo", "s", new(start: 0, increment: increment,
            minimum: long.MinValue, maximum: long.MaxValue));

        // Act / Assert
        database.Catalog.AllocateSequence(sequence).ShouldBe(0);
        database.Catalog.AllocateSequence(sequence).ShouldBe(increment);
        Should.Throw<SqlCatalogException>(() => database.Catalog.AllocateSequence(sequence)).Diagnostics.Single().Code.ShouldBe("CS02002");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence: catalog publications share the relation namespace in both directions")]
    public async Task Catalog_RelationCollisionAndSchemaDrop_ShouldPreserveNamespace()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-names", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var catalog = database.Catalog;
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW v AS SELECT id FROM t", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE free (id INT)", cancellationToken: CancellationToken.None);
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateSequenceAsync("dbo", "T", new()).AsTask());
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateSequenceAsync("dbo", "V", new()).AsTask());
        var sequence = await catalog.CreateSequenceAsync("dbo", "S", new(cache: long.MaxValue));
        var snapshot = catalog.Sequences;
        ulong generation = catalog.DdlGeneration;

        // Act / Assert
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateSequenceAsync("missing", "s", new()).AsTask());
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.CreateSequenceAsync("dbo", "s", new()).AsTask());
        foreach (string sql in new[] { "CREATE TABLE s (id INT)", "CREATE VIEW s AS SELECT id FROM t", "ALTER TABLE free RENAME TO s", "CREATE TABLE s AS SELECT id FROM t" })
        { await Should.ThrowAsync<DatabaseException>(async () => { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }); }
        catalog.DdlGeneration.ShouldBe(generation);
        catalog.AllocateSequence(sequence).ShouldBe(1);
        catalog.AllocateSequence(sequence).ShouldBe(2);
        catalog.DdlGeneration.ShouldBe(generation);
        snapshot.ShouldHaveSingleItem().ShouldBeSameAs(sequence);
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.DropSchemaAsync("dbo").AsTask());
        await catalog.DropSchemaAsync("dbo", cascade: true);
        catalog.Sequences.ShouldBeEmpty();
        Should.Throw<SqlCatalogException>(() => catalog.AllocateSequence(sequence)).Diagnostics.Single().Code.ShouldBe("CS04067");
        snapshot.ShouldHaveSingleItem().Options.Cache.ShouldBe(long.MaxValue);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence: rollback consumes values and replacement/restart publish atomically")]
    public async Task Catalog_RollbackAlterRestartAndDrop_ShouldKeepIndependentAllocation()
    {
        // Arrange
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("sequence-alter", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(start: 5, increment: 3, minimum: -20, maximum: 1000, cache: 9));

        // Act
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        catalog.AllocateSequence(sequence).ShouldBe(5);
        await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
        catalog.AllocateSequence(sequence).ShouldBe(8);
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.AlterSequenceAsync(sequence, sequence.Options, restart: 1001).AsTask());
        var changed = await catalog.AlterSequenceAsync(sequence, new(start: 5, increment: 2, minimum: -20, maximum: 1000, cache: 10));
        var restarted = await catalog.AlterSequenceAsync(changed, changed.Options, restart: -10);
        var image = strategy.CaptureDurableImages();

        // Assert
        Should.Throw<SqlCatalogException>(() => catalog.AllocateSequence(sequence)).Diagnostics.Single().Code.ShouldBe("CS04067");
        catalog.AllocateSequence(restarted).ShouldBe(-10);
        await using var reopened = SqlDatabaseEngine.Create("sequence-restart", new() { StorageStrategy = image });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        var current = restored.Catalog.FindSequence("DBO", "S").ShouldNotBeNull();
        current.Options.Start.ShouldBe(5);
        current.Options.Cache.ShouldBe(10);
        restored.Catalog.AllocateSequence(current).ShouldBe(-10);
        await catalog.DropSequenceAsync(restarted);
        await Should.ThrowAsync<SqlCatalogException>(() => catalog.DropSequenceAsync(restarted).AsTask());
        var recreated = await catalog.CreateSequenceAsync("dbo", "s", new());
        recreated.ObjectId.ShouldNotBe(restarted.ObjectId);
        catalog.AllocateSequence(recreated).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: reservation faults release the catalog monitor and never return an uncovered value")]
    [InlineData(false, false)][InlineData(true, false)][InlineData(false, true)][InlineData(true, true)]
    public async Task AllocateSequence_ReservationHookThrows_ShouldReleaseAndRecover(bool afterCommit, bool identity)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("sequence-hook", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new());
        var table = await catalog.CreateTableAsync("dbo", "ids", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int64),
            identity: new SqlCatalogIdentity(false))]);
        long Allocate() => identity ? catalog.AllocateIdentity(table) : catalog.AllocateSequence(sequence);
        int observed = 0;
        Action fail = () => { observed++; throw new InvalidOperationException("Injected allocation boundary failure."); };
        if (afterCommit) { catalog.ReservationCommitted = fail; } else { catalog.ReservationCommitting = fail; }

        // Act
        Should.Throw<InvalidOperationException>(() => Allocate());
        catalog.ReservationCommitting = null;
        catalog.ReservationCommitted = null;
        var image = strategy.CaptureDurableImages();
        long value = await DedicatedTestTask.Run(() => Task.FromResult(Allocate()), timeout.Token);

        // Assert
        observed.ShouldBe(1);
        value.ShouldBe(1);
        await using var reopened = SqlDatabaseEngine.Create("sequence-hook-recover", new() { StorageStrategy = image });
        var restored = await reopened.OpenDatabaseAsync("db", timeout.Token);
        restored.Catalog.TryGetTable("dbo", "ids", out var restoredTable).ShouldBeTrue();
        (identity ? restored.Catalog.AllocateIdentity(restoredTable) :
            restored.Catalog.AllocateSequence(restored.Catalog.FindSequence("dbo", "s")!)).ShouldBe(afterCommit ? 33 : 1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence: a paused allocator cannot use an incarnation dropped before it enters")]
    public async Task AllocateSequence_ConcurrentDrop_ShouldRefuseRetiredIncarnation()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("sequence-drop-race", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(), timeout.Token);
        catalog.AllocationEntering = () => { entered.Set(); release.Wait(timeout.Token); };
        var actor = DedicatedTestTask.Run(() => Task.FromResult(catalog.AllocateSequence(sequence)), timeout.Token);
        try
        {
            // Act
            entered.Wait(timeout.Token);
            catalog.AllocationEntering = null;
            await catalog.DropSequenceAsync(sequence, timeout.Token);
            var replacement = await catalog.CreateSequenceAsync("dbo", "s", new(), timeout.Token);
            release.Set();

            // Assert
            (await Should.ThrowAsync<SqlCatalogException>(() => actor)).Diagnostics.Single().Code.ShouldBe("CS04067");
            catalog.AllocateSequence(replacement).ShouldBe(1);
        }
        finally
        {
            catalog.AllocationEntering = null;
            release.Set();
            try { await actor; } catch (Exception) { /* Observe the actor without replacing the test failure. */ }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence: cancellation while a reservation holds publication releases every mutation path")]
    public async Task Catalog_CancellationDuringAllocation_ShouldLeavePublicationAndCursorUsable()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var reserved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var mutationStarted = new CountdownEvent(3);
        await using var engine = SqlDatabaseEngine.Create("sequence-cancel-race", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(), timeout.Token);
        ulong generation = catalog.DdlGeneration;
        catalog.ReservationCommitting = () => { reserved.Set(); release.Wait(timeout.Token); };
        var allocation = DedicatedTestTask.Run(() => Task.FromResult(catalog.AllocateSequence(sequence)), timeout.Token);
        var mutations = new List<Task>();
        try
        {
            reserved.Wait(timeout.Token);
            mutations.Add(DedicatedTestTask.Run(async () => { mutationStarted.Signal(); await catalog.CreateSequenceAsync("dbo", "other", new(), canceled.Token); }, timeout.Token));
            mutations.Add(DedicatedTestTask.Run(async () => { mutationStarted.Signal(); await catalog.AlterSequenceAsync(sequence, new(), cancellationToken: canceled.Token); }, timeout.Token));
            mutations.Add(DedicatedTestTask.Run(async () => { mutationStarted.Signal(); await catalog.DropSequenceAsync(sequence, canceled.Token); }, timeout.Token));

            // Act
            mutationStarted.Wait(timeout.Token);
            canceled.Cancel();
            release.Set();

            // Assert
            (await allocation).ShouldBe(1);
            foreach (var mutation in mutations) { await Should.ThrowAsync<OperationCanceledException>(() => mutation); }
            catalog.DdlGeneration.ShouldBe(generation);
            catalog.Sequences.ShouldHaveSingleItem().ShouldBeSameAs(sequence);
            catalog.AllocateSequence(sequence).ShouldBe(2);
        }
        finally
        {
            catalog.ReservationCommitting = null;
            release.Set();
            try { await Task.WhenAll(mutations.Append(allocation)); } catch (Exception) { /* Observe all actors on failure and cancellation. */ }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence: unconfirmed durability fences all allocation until reopen")]
    public async Task AllocateSequence_UnconfirmedCommit_ShouldFenceAndRecover()
    {
        // Arrange
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("sequence-fence", new() { StorageStrategy = strategy, Durability = StorageCommitDurability.Grouped });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new());
        int pending = 0;
        database.CatalogStorage.OnCommitPending = () =>
        { database.CatalogStorage.FlushPendingCommits(); pending++; throw new NotSupportedException("Injected reservation wait failure."); };

        // Act / Assert
        Should.Throw<StorageOfflineException>(() => catalog.AllocateSequence(sequence));
        pending.ShouldBe(1);
        Should.Throw<StorageOfflineException>(() => catalog.AllocateSequence(sequence));
        await Should.ThrowAsync<StorageOfflineException>(() => catalog.DropSequenceAsync(sequence).AsTask());
        var restored = await engine.OpenDatabaseAsync("db", CancellationToken.None);
        restored.Catalog.AllocateSequence(restored.Catalog.FindSequence("dbo", "s")!).ShouldBe(33);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: every create, reservation and drop crash image is atomic and skips handed values")]
    [InlineData(StorageCommitDurability.Synchronous)][InlineData(StorageCommitDurability.Grouped)]
    public async Task Recovery_AllWriteBoundaries_ShouldNeverReuseHandedValues(StorageCommitDurability durability)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var strategy = new CrashCaptureSqlStorageStrategy(durability);
        var phases = new List<(CrashCaptureSqlStorageStrategy.CrashPointRecorder Recorder, long Handed, string Phase)>();
        await using (var engine = SqlDatabaseEngine.Create("sequence-crash", new() { StorageStrategy = strategy, Durability = durability }))
        {
            var database = await engine.CreateDatabaseAsync("db", timeout.Token);
            var catalog = database.Catalog;
            SqlCatalogSequence sequence;
            using (var recorder = strategy.RecordCrashPoints())
            {
                sequence = await catalog.CreateSequenceAsync("dbo", "s", new());
                phases.Add((recorder, 0, "create"));
            }
            using (var recorder = strategy.RecordCrashPoints())
            {
                catalog.AllocateSequence(sequence).ShouldBe(1);
                phases.Add((recorder, 0, "first reservation"));
            }
            for (int value = 2; value <= 32; value++) { catalog.AllocateSequence(sequence).ShouldBe(value); }
            using (var recorder = strategy.RecordCrashPoints())
            {
                catalog.AllocateSequence(sequence).ShouldBe(33);
                phases.Add((recorder, 32, "second reservation"));
            }
            catalog.AllocateSequence(sequence).ShouldBe(34);
            using (var recorder = strategy.RecordCrashPoints())
            {
                database.CatalogStorage.Checkpoint();
                phases.Add((recorder, 34, "partly issued checkpoint"));
            }
            using (var recorder = strategy.RecordCrashPoints())
            {
                await catalog.DropSequenceAsync(sequence, timeout.Token);
                phases.Add((recorder, 34, "drop"));
            }
        }

        // Act / Assert
        int present = 0, absent = 0, images = 0;
        foreach (var (recorder, handed, phase) in phases)
        {
            recorder.Count.ShouldBeGreaterThan(1);
            for (int point = 0; point < recorder.Count; point++)
            {
                await using var recovered = SqlDatabaseEngine.Create("sequence-recover", new() { StorageStrategy = recorder.Open(point), Durability = durability });
                var database = await recovered.OpenDatabaseAsync("db", timeout.Token);
                var sequence = database.Catalog.FindSequence("dbo", "s");
                images++;
                if (sequence is null) { absent++; (phase is "create" or "drop").ShouldBeTrue(); continue; }
                present++;
                database.Catalog.AllocateSequence(sequence).ShouldBeGreaterThan(handed);
                if (handed == 34) { database.Catalog.AllocateSequence(sequence).ShouldBe(66); }
            }
        }
        present.ShouldBeGreaterThan(0);
        absent.ShouldBeGreaterThan(0);
        _output.WriteLine($"{durability}: images={images}, present={present}, absent={absent}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence: many sessions contend deterministically and keep shared allocation unique")]
    [InlineData(false)][InlineData(true)]
    public async Task Allocation_ConcurrentSessions_ShouldSerializeReservationsAndReleaseAllPaths(bool identity)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var reserved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var entering = new CountdownEvent(6);
        await using var engine = SqlDatabaseEngine.Create("sequence-race", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE ids (id BIGINT IDENTITY PRIMARY KEY)", cancellationToken: timeout.Token);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(), timeout.Token);
        int entries = 0, reservations = 0, allocations = 0;
        var values = new ConcurrentBag<long>();
        catalog.AllocationEntering = () =>
        {
            if (Interlocked.Increment(ref entries) <= 6)
            {
                entering.Signal();
                // Every session must finish catalog-dependent planning before the
                // first allocator holds the catalog monitor at reservation commit.
                entering.Wait(timeout.Token);
            }
        };
        catalog.ReservationCommitting = () =>
        { if (Interlocked.Increment(ref reservations) == 1) { reserved.Set(); release.Wait(timeout.Token); } };
        var elapsed = Stopwatch.StartNew();
        var actors = Enumerable.Range(0, 6).Select(_ => DedicatedTestTask.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync(timeout.Token);
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (identity)
                { await session.ExecuteAsync("INSERT INTO ids DEFAULT VALUES", cancellationToken: timeout.Token); }
                else { values.Add(catalog.AllocateSequence(sequence)); }
                Interlocked.Increment(ref allocations);
                await Task.Yield();
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || Volatile.Read(ref allocations) < 192 || Volatile.Read(ref reservations) < 6);
        }, timeout.Token)).ToArray();
        try
        {
            // Act: the first reservation holds the catalog while all six callers enter.
            reserved.Wait(timeout.Token);
            entering.Wait(timeout.Token);
            Volatile.Read(ref allocations).ShouldBe(0);
            release.Set();
            await Task.WhenAll(actors);

            // Assert
            entries.ShouldBe(allocations);
            reservations.ShouldBeGreaterThanOrEqualTo(6);
            allocations.ShouldBeGreaterThanOrEqualTo(192);
            if (identity)
            {
                await using var result = (DatabaseResultSet)await setup.ExecuteAsync("SELECT id FROM ids", cancellationToken: timeout.Token);
                var ids = new List<long>();
                await foreach (var row in result.GetRowsAsync(timeout.Token)) { ids.Add(Convert.ToInt64(row.GetValue(0))); }
                ids.Count.ShouldBe(allocations);
                ids.Distinct().Count().ShouldBe(allocations);
            }
            else { values.Distinct().Count().ShouldBe(allocations); }
            _output.WriteLine($"identity={identity}: entries={entries}, reservations={reservations}, allocations={allocations}");
        }
        finally
        {
            release.Set();
            catalog.AllocationEntering = null;
            catalog.ReservationCommitting = null;
            try { await Task.WhenAll(actors); } catch (Exception) { /* Observe actors without replacing the original failure. */ }
        }
    }
}
