using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SqlIdentityConcurrency")]
public sealed class SqlSequenceReviewTests
{
    private readonly ITestOutputHelper _output;

    public SqlSequenceReviewTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence docs: format history and current format agree with the gate")]
    [InlineData(false)][InlineData(true)]
    public void Documentation_SqlFormat_ShouldNameCurrentAndIntroducedVersions(bool indexing)
    {
        // Arrange
        string root = DatabaseDirectory();
        string file = Path.Combine(root, indexing ? "Assimalign.Cohesion.Database.Indexing" : "Assimalign.Cohesion.Database.Sql.Language",
            "docs", indexing ? "DESIGN.md" : "DIALECT.md");

        // Act
        string document = File.ReadAllText(file);

        // Assert
        document.ShouldNotContain("current format 25", Case.Insensitive);
        document.ShouldNotContain("current record-space format is 25", Case.Insensitive);
        if (indexing) { document.ShouldContain("current format 34", Case.Sensitive); }
        else
        {
            document.ShouldContain("current record-space format is 34", Case.Sensitive);
            document.ShouldContain("SQL format 27 introduced the per-column TYPE default-assignment flag", Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence docs: registry rows retain their SQLSTATEs")]
    [InlineData("CS03069", "22023")][InlineData("CS03070", "42P07")][InlineData("CS03071", "3F000")]
    [InlineData("CS03072", "54000")][InlineData("CS04067", "40001")]
    public void Documentation_SequenceRegistry_ShouldIncludeSqlState(string code, string state)
    {
        // Arrange
        string file = Path.Combine(DatabaseDirectory(), "Assimalign.Cohesion.Database", "docs", "ERROR-CODES.md");

        // Act
        string row = File.ReadLines(file).Single(line => line.Contains($"| {code} |", StringComparison.Ordinal));

        // Assert
        row.ShouldContain(state, Case.Sensitive);
    }

    private static string DatabaseDirectory([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence ALTER: bounds validate the last fetched value")]
    [InlineData(false)][InlineData(true)]
    public async Task AlterSequence_FetchedValueBounds_ShouldIgnoreReservationTail(bool raiseMinimum)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-bounds-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(minimum: 1, maximum: 1000));
        catalog.AllocateSequence(sequence).ShouldBe(1);

        // Act / Assert
        if (raiseMinimum)
        {
            var failure = await Should.ThrowAsync<SqlCatalogException>(() => catalog.AlterSequenceAsync(sequence,
                new(start: 20, minimum: 20, maximum: 1000)).AsTask());
            failure.Diagnostics.Single().Code.ShouldBe("CS03069");
            catalog.AllocateSequence(sequence).ShouldBe(2);
        }
        else
        {
            var updated = await catalog.AlterSequenceAsync(sequence, new(minimum: 1, maximum: 10));
            catalog.AllocateSequence(updated).ShouldBe(2);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence ALTER: cache changes preserve unissued values and the recovered endpoint")]
    [InlineData(false)][InlineData(true)]
    public async Task AlterSequence_CacheOnly_ShouldKeepFetchedEndpoint(bool cycle)
    {
        // Arrange
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("alter-cache-review", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(minimum: 1, maximum: 40, cycle: cycle));
        for (int value = 1; value <= 33; value++) { catalog.AllocateSequence(sequence).ShouldBe(value); }

        // Act
        var updated = await catalog.AlterSequenceAsync(sequence, new(minimum: 1, maximum: 40, cache: 9, cycle: cycle));
        var image = strategy.CaptureDurableImages();

        // Assert
        await using var recovered = SqlDatabaseEngine.Create("alter-cache-recovered", new() { StorageStrategy = image });
        var restored = await recovered.OpenDatabaseAsync("db", CancellationToken.None);
        restored.Catalog.AllocateSequence(restored.Catalog.FindSequence("dbo", "s")!).ShouldBe(34);
        for (int value = 34; value <= 40; value++) { catalog.AllocateSequence(updated).ShouldBe(value); }
        if (cycle) { catalog.AllocateSequence(updated).ShouldBe(1); }
        else { Should.Throw<SqlCatalogException>(() => catalog.AllocateSequence(updated)).Diagnostics.Single().Code.ShouldBe("CS02002"); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence recovery: every ALTER and RESTART storage write restores one complete cursor")]
    [InlineData(StorageCommitDurability.Synchronous)][InlineData(StorageCommitDurability.Grouped)]
    public async Task Recovery_AlterRestartWriteBoundaries_ShouldKeepCompleteFetchedState(StorageCommitDurability durability)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var strategy = new CrashCaptureSqlStorageStrategy(durability);
        await using var engine = SqlDatabaseEngine.Create("alter-restart-crash-review", new() { StorageStrategy = strategy, Durability = durability });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new(start: 1, minimum: -20, maximum: 1000));
        for (int value = 1; value <= 33; value++) { catalog.AllocateSequence(sequence).ShouldBe(value); }

        // Act
        CrashCaptureSqlStorageStrategy.CrashPointRecorder alter;
        using (alter = strategy.RecordCrashPoints())
        { sequence = await catalog.AlterSequenceAsync(sequence, new(start: 1, minimum: -20, maximum: 1000, cache: 9)); }
        CrashCaptureSqlStorageStrategy.CrashPointRecorder restart;
        using (restart = strategy.RecordCrashPoints())
        { await catalog.AlterSequenceAsync(sequence, sequence.Options, restart: -10); }

        // Assert
        int images = 0, oldAlter = 0, newAlter = 0, oldRestart = 0, newRestart = 0;
        foreach (var recorder in new[] { alter, restart })
        {
            recorder.Count.ShouldBeGreaterThan(2);
            for (int point = 0; point < recorder.Count; point++)
            {
                await using var recovered = SqlDatabaseEngine.Create("alter-restart-restored", new() { StorageStrategy = recorder.Open(point), Durability = durability });
                var restored = await recovered.OpenDatabaseAsync("db", timeout.Token);
                var current = restored.Catalog.FindSequence("dbo", "s").ShouldNotBeNull();
                long value = restored.Catalog.AllocateSequence(current);
                if (ReferenceEquals(recorder, alter))
                {
                    if (current.Options.Cache == 1) { value.ShouldBe(65); oldAlter++; }
                    else { current.Options.Cache.ShouldBe(9); value.ShouldBe(34); newAlter++; }
                }
                else
                {
                    current.Options.Cache.ShouldBe(9);
                    value.ShouldBeOneOf(34L, -10L);
                    if (value == 34) { oldRestart++; } else { newRestart++; }
                    if (point == recorder.Count - 1) { value.ShouldBe(-10); }
                }
                images++;
            }
        }
        oldAlter.ShouldBeGreaterThan(0); newAlter.ShouldBeGreaterThan(0);
        oldRestart.ShouldBeGreaterThan(0); newRestart.ShouldBeGreaterThan(0);
        _output.WriteLine($"{durability}: images={images}, ALTER old={oldAlter}, new={newAlter}, RESTART old={oldRestart}, new={newRestart}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Allocator: engine disposal invalidates cached sequence and identity values")]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task Allocate_EngineDisposed_ShouldRefuseCachedValues(bool identity, bool synchronous)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("disposed-allocation-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new());
        var table = await catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int64),
            identity: new SqlCatalogIdentity(false))]);
        long Allocate() => identity ? catalog.AllocateIdentity(table) : catalog.AllocateSequence(sequence);
        Allocate().ShouldBe(1);

        // Act
        if (synchronous) { engine.Dispose(); } else { await engine.DisposeAsync(); }

        // Assert
        Should.Throw<ObjectDisposedException>(() => Allocate());
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence decode: truncated definition and counter retain their corruption identity")]
    [InlineData(18)][InlineData(19)]
    public void Open_TruncatedSequenceRecord_ShouldReportSequenceCorruption(int kind)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "truncated-sequence-review");
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, new DatabaseKeyWriter().AppendInt32(kind).AppendInt64(1).ToArray());
            transaction.Commit();
        }

        // Act / Assert
        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06061");
        failure.InnerException.ShouldBeOfType<StorageCorruptionException>().InnerException.ShouldBeOfType<SqlCatalogException>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence namespace: collisions use the relation code and identify the existing kind")]
    [InlineData(0, "sequence")][InlineData(1, "sequence")][InlineData(2, "sequence")]
    [InlineData(3, "sequence")][InlineData(4, "table")][InlineData(5, "view")][InlineData(6, "sequence")]
    public async Task Catalog_SequenceRelationCollision_ShouldNameExistingKind(int operation, string kind)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("relation-code-review", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE free (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW v AS SELECT id FROM t", cancellationToken: CancellationToken.None);
        await database.Catalog.CreateSequenceAsync("dbo", "s", new());

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (operation >= 4)
            { await database.Catalog.CreateSequenceAsync("dbo", operation == 4 ? "t" : operation == 5 ? "v" : "s", new()); }
            else
            {
                string sql = operation switch
                {
                    0 => "CREATE TABLE s (id INT)", 1 => "CREATE TABLE s AS SELECT id FROM t",
                    2 => "ALTER TABLE free RENAME TO s", _ => "CREATE VIEW s AS SELECT id FROM t",
                };
                await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            }
        });

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03070");
        failure.Message.ShouldContain($"{kind} '", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Planner: catalog generation reads proceed during a held reservation commit")]
    public async Task Plan_ReservationCommitHeld_ShouldCompleteBeforeRelease()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("planning-reservation-review", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var catalog = database.Catalog;
        var sequence = await catalog.CreateSequenceAsync("dbo", "s", new());
        var expression = SqlQueryRequest.FromSql("SELECT 1").Statement.SqlExpression;
        int commits = 0;
        catalog.ReservationCommitting = () => { Interlocked.Increment(ref commits); entered.Set(); release.Wait(timeout.Token); };
        var allocation = DedicatedTestTask.Run(() => Task.FromResult(catalog.AllocateSequence(sequence)), timeout.Token);
        Task<SqlPlan>? planning = null;
        try
        {
            entered.Wait(timeout.Token);

            // Act: completion is required while the reservation still owns the monitor.
            planning = DedicatedTestTask.Run(() => Task.FromResult(new SqlPlanner(catalog, null).Plan(expression)), timeout.Token);
            var plan = await planning.WaitAsync(timeout.Token);

            // Assert
            plan.ShouldNotBeNull();
            release.IsSet.ShouldBeFalse();
            commits.ShouldBe(1);
            _output.WriteLine("Plan completed while the first reservation commit was held; release=false, commits=1.");
        }
        finally
        {
            catalog.ReservationCommitting = null;
            release.Set();
            try { await allocation; } catch (Exception) { /* Observe the actor without replacing the test failure. */ }
            if (planning is not null) { try { await planning; } catch (Exception) { /* Observe the planner on failure. */ } }
        }
    }
}
