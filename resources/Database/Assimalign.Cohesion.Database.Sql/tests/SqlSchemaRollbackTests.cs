using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlSchemaRollbackTests
{
    private readonly ITestOutputHelper _output;

    public SqlSchemaRollbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback: additive publication holds every reverse step regardless of opt-in or inline Build")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BuildAsync_AdditiveRollback_ShouldHoldStoredSchema(bool optIn, bool inline)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Declare(extra: false, optIn: optIn);
        var second = Declare(extra: true);
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", timeout.Token);
            await database.ApplySchemaAsync(first.Compile(), timeout.Token);
            await database.ApplySchemaAsync(second.Compile(), timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync("INSERT INTO items (Id, Value, Added) VALUES (1, 42, 9)", cancellationToken: timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using (var old = await Builder(storage, first, inline).BuildAsync(timeout.Token))
        {
            var database = await old.OpenDatabaseAsync("history", timeout.Token);
            var generation = database.Catalog.DdlGeneration;

            // Assert: no safe prefix (DropIndex), intent, or marker publication ran.
            database.IsOffline.ShouldBeFalse();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Catalog.AppliedSchemaHistory.Length.ShouldBe(2);
            database.Provisioning!.FailureCode.ShouldBe("CS03405");
            database.Provisioning.Detail!.ShouldContain("DropColumn 'items.Added'", Case.Sensitive);
            database.Provisioning.Detail!.ShouldContain(first.Compile().Hash, Case.Sensitive);
            database.Provisioning.Detail!.ShouldContain(second.Compile().Hash, Case.Sensitive);
            database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
            database.Catalog.GetIndexes(table.ObjectId).ShouldNotBeEmpty();
            await using var session = await database.CreateSessionAsync(timeout.Token);
            (await RowsAsync(session, "SELECT Added FROM items WHERE Id=1", timeout.Token)).Single()[0].ShouldBe(9);
            database.Catalog.DdlGeneration.ShouldBe(generation);
        }
        await using var current = await Builder(storage, second, inline).BuildAsync(timeout.Token);
        (await current.OpenDatabaseAsync("history", timeout.Token)).Provisioning.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback: re-adding either physical column position and reversing defaults holds before rendering")]
    [InlineData("trailing")]
    [InlineData("middle")]
    [InlineData("default")]
    public async Task BuildAsync_PublishedDropOrDefault_ShouldHoldBeforeGeneratingScript(string kind)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool target) => SqlSchema.Create("history", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<Row>("items", table =>
            {
                table.Key(row => row.Id);
                if (kind != "middle" || !target) { table.Column(row => row.Value); }
                if (kind != "trailing" || !target) { table.Column(row => row.Added); }
                if (kind == "default") { table.Default(row => row.Value, target ? 2 : 1); }
            });
        });
        var first = Version(false);
        var second = Version(true);
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", timeout.Token);
            await database.ApplySchemaAsync(first.Compile(), timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES (1, 42, 9)", cancellationToken: timeout.Token);
            await database.ApplySchemaAsync(second.Compile(), timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using var rollback = await Builder(storage, first).BuildAsync(timeout.Token);
        var held = await rollback.OpenDatabaseAsync("history", timeout.Token);

        // Assert
        held.Provisioning!.FailureCode.ShouldBe("CS03405");
        held.Provisioning.Detail!.ShouldContain(kind == "default" ? "AlterColumn" : "AddColumn", Case.Sensitive);
        held.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
        held.Catalog.PendingMigration.ShouldBeNull();
        await using var reader = await held.CreateSessionAsync(timeout.Token);
        if (kind != "default")
        {
            string surviving = kind == "middle" ? "Added" : "Value";
            (await RowsAsync(reader, "SELECT " + surviving + " FROM items WHERE Id=1", timeout.Token)).Single()[0].ShouldBe(kind == "middle" ? 9 : 42);
            string missing = kind == "middle" ? "Value" : "Added";
            held.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
            table.FindColumn(missing).ShouldBeNull();
            await Should.ThrowAsync<DatabaseException>(async () => await reader.ExecuteAsync("SELECT " + missing + " FROM items", cancellationToken: timeout.Token));
        }
        else
        {
            (await RowsAsync(reader, "SELECT Value, Added FROM items WHERE Id=1", timeout.Token)).Single().ShouldBe([42, 9]);
            await reader.ExecuteAsync("INSERT INTO items (Id, Added) VALUES (2,9)", cancellationToken: timeout.Token);
            (await RowsAsync(reader, "SELECT Value FROM items WHERE Id=2", timeout.Token)).Single()[0].ShouldBe(2);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rollback: a lossless index reversal publishes and updates only last-applied generation")]
    public async Task BuildAsync_LosslessRollback_ShouldPublishAndRetainFirstGeneration()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Declare(false, index: true);
        var second = Declare(false);
        ulong firstGeneration;
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", timeout.Token);
            await database.ApplySchemaAsync(first.Compile(), timeout.Token);
            firstGeneration = database.Catalog.AppliedSchemaHistory.Single().FirstAppliedGeneration;
            await database.ApplySchemaAsync(second.Compile(), timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using var rollback = await Builder(storage, first).BuildAsync(timeout.Token);
        var restored = await rollback.OpenDatabaseAsync("history", timeout.Token);

        // Assert
        restored.Provisioning.ShouldBeNull();
        restored.Catalog.SchemaState!.ContentHash.ShouldBe(first.Compile().Hash);
        var entry = restored.Catalog.AppliedSchemaHistory.Single(value => value.ContentHash == first.Compile().Hash);
        entry.FirstAppliedGeneration.ShouldBe(firstGeneration);
        entry.LastAppliedGeneration.ShouldBe(restored.Catalog.DdlGeneration);
        entry.LastAppliedGeneration.ShouldBeGreaterThan(firstGeneration);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hold: never-seen destructive declaration stays available; Verify and explicit apply remain strict")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_UnseenDestructiveWithoutOptIn_ShouldHoldAndKeepStrictPaths(bool inline)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var stored = Declare(true);
        var unseen = Declare(false);
        await using (var seed = await Builder(storage, stored).BuildAsync(timeout.Token))
        {
            var database = await seed.OpenDatabaseAsync("history", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,42,9)", cancellationToken: timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using (var held = await Builder(storage, unseen, inline).BuildAsync(timeout.Token))
        {
            var database = await held.OpenDatabaseAsync("history", timeout.Token);

            // Assert
            database.Provisioning!.FailureCode.ShouldBe("CS03105");
            database.Provisioning.Detail!.ShouldContain("DropColumn 'items.Added'", Case.Sensitive);
            database.Catalog.AppliedSchemaHistory.Length.ShouldBe(1);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(stored.Compile().Hash);
            await using var reader = await database.CreateSessionAsync(timeout.Token);
            (await RowsAsync(reader, "SELECT Value, Added FROM items", timeout.Token)).Single().ShouldBe([42, 9]);
            await using var explicitEngine = await Builder(storage.CaptureDurableImages()).BuildAsync(timeout.Token);
            var explicitDatabase = await explicitEngine.OpenDatabaseAsync("history", timeout.Token);
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await explicitDatabase.ApplySchemaAsync(unseen.Compile(), timeout.Token));
            failure.Diagnostics.ShouldContain(value => value.Code == "CS03105");
        }
        var verify = Builder(storage);
        verify.AddDatabase("history", database => { database.Schema(unseen); database.Provisioning = SqlProvisioningMode.Verify; });
        var drift = await Should.ThrowAsync<SqlSchemaMigrationException>(() => verify.BuildAsync(timeout.Token).AsTask());
        drift.Message.ShouldContain("CS03103", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback writes: stored NOT NULL and defaults govern old-application inserts")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_OldApplicationWrites_ShouldEnforceStoredSchema(bool withDefault)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Declare(false);
        var second = Declare(true, optIn: true, required: true, withDefault: withDefault);
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var seeded = await seed.CreateDatabaseAsync("history", timeout.Token);
            await seeded.ApplySchemaAsync(first.Compile(), timeout.Token);
            if (!withDefault) { await seeded.ApplySchemaAsync(Declare(true, optIn: true, required: true, withDefault: true).Compile(), timeout.Token); }
            await seeded.ApplySchemaAsync(second.Compile(), timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using var old = await Builder(storage, first).BuildAsync(timeout.Token);
        var database = await old.OpenDatabaseAsync("history", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);

        // Assert
        database.Provisioning!.FailureCode.ShouldBe("CS03405");
        if (withDefault)
        {
            await session.ExecuteAsync("INSERT INTO items (Id, Value) VALUES (1, 42)", cancellationToken: timeout.Token);
            (await RowsAsync(session, "SELECT Added FROM items", timeout.Token)).Single()[0].ShouldBe(7);
        }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO items (Id, Value) VALUES (1, 42)", cancellationToken: timeout.Token));
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03024");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - History: 1000 distinct identities cross chunk boundaries without evicting the first")]
    public async Task SaveSchemaStateAsync_ThousandSchemas_ShouldChunkAndRetainEveryIdentity()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        string document = Declare(false).Compile().CanonicalDocument;
        // Act: reopen between non-aligned batches to retain exactly one partial chunk
        // even when catalog relocation changes physical record order.
        for (int first = 0; first < 1000; first += 17)
        {
            await using var seed = await Builder(storage).BuildAsync(timeout.Token);
            var database = first == 0 ? await seed.CreateDatabaseAsync("history", timeout.Token)
                : await seed.OpenDatabaseAsync("history", timeout.Token);
            for (int index = first; index < Math.Min(first + 17, 1000); index++)
            { await database.Catalog.SaveSchemaStateAsync(new(index.ToString("x64"), document, database.Catalog.DdlGeneration), timeout.Token); }
            if (first + 17 >= 1000)
            { await database.Catalog.SaveSchemaStateAsync(new(0.ToString("x64"), document, database.Catalog.DdlGeneration), timeout.Token); }
            storage = storage.CaptureDurableImages();
        }
        await using var reopened = await Builder(storage).BuildAsync(timeout.Token);
        var catalog = (await reopened.OpenDatabaseAsync("history", timeout.Token)).Catalog;

        // Assert
        catalog.AppliedSchemaHistory.Length.ShouldBe(1000);
        catalog.HasAppliedSchema(0.ToString("x64")).ShouldBeTrue();
        catalog.HasAppliedSchema(999.ToString("x64")).ShouldBeTrue();
        int chunks = 0, bytes = 0;
        using var units = (await reopened.OpenDatabaseAsync("history", timeout.Token)).CatalogStorage.GetUnitIterator();
        while (units.MoveNext())
        {
            var reader = new DatabaseKeyReader(units.Current.Data.Span);
            if (reader.ReadInt32() != 20) { continue; }
            chunks++;
            bytes += units.Current.Data.Length;
            units.Current.Data.Length.ShouldBeLessThanOrEqualTo(SlottedPage.MaxRecordSize);
        }
        chunks.ShouldBe(32);
        bytes.ShouldBe(86320);
        _output.WriteLine($"1000 distinct schemas: {bytes} encoded bytes across {chunks} chunks; one-record limit {SlottedPage.MaxRecordSize} bytes.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - History: oversized caller identity refuses the whole publication with CS05042")]
    public async Task SaveSchemaStateAsync_OversizedHistoryChunk_ShouldRollbackWithoutEviction()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("history", timeout.Token);
        string document = Declare(false).Compile().CanonicalDocument;
        for (int index = 0; index < 31; index++)
        { await database.Catalog.SaveSchemaStateAsync(new(index.ToString("x64"), document, database.Catalog.DdlGeneration), timeout.Token); }
        string previous = database.Catalog.SchemaState!.ContentHash;

        // Act: kind 6 fits, but the history chunk would exceed its one-record limit.
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () =>
            await database.Catalog.SaveSchemaStateAsync(new(new string('x', 6000), document, database.Catalog.DdlGeneration), timeout.Token));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05042");
        database.Catalog.SchemaState!.ContentHash.ShouldBe(previous);
        database.Catalog.AppliedSchemaHistory.Length.ShouldBe(31);
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(timeout.Token);
        var catalog = (await reopened.OpenDatabaseAsync("history", timeout.Token)).Catalog;
        catalog.SchemaState!.ContentHash.ShouldBe(previous);
        catalog.AppliedSchemaHistory.Length.ShouldBe(31);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - History corruption: malformed counts, duplicates and generation ranges refuse reopen")]
    [InlineData("count")]
    [InlineData("duplicate")]
    [InlineData("range")]
    [InlineData("future")]
    public async Task OpenAsync_MalformedHistory_ShouldRefuseCorruption(string defect)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var seed = await Builder(storage).BuildAsync(timeout.Token);
        var database = await seed.CreateDatabaseAsync("history", timeout.Token);
        var schema = Declare(false).Compile();
        await database.ApplySchemaAsync(schema, timeout.Token);
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(20).AppendInt32(defect == "count" ? 0 : 1);
        if (defect != "count")
        {
            writer.AppendString(defect == "duplicate" ? schema.Hash : "damaged", Collation.Binary)
                .AppendInt64(defect == "range" ? 2 : 0)
                .AppendInt64(defect == "future" ? checked((long)database.Catalog.DdlGeneration + 1) : 1);
        }
        using (var transaction = database.CatalogStorage.BeginTransaction())
        {
            database.CatalogStorage.InsertRow(transaction, writer.ToArray());
            transaction.Commit();
        }

        // Act
        await using var reopening = await Builder(storage.CaptureDurableImages()).BuildAsync(timeout.Token);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopening.OpenDatabaseAsync("history", timeout.Token));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS06059");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - History crash corpus: every durable boundary converges kind 6 and history together")]
    [InlineData("first")]
    [InlineData("rollover")]
    [InlineData("save")]
    [InlineData("inline")]
    [InlineData("deferred")]
    public async Task ApplyAsync_EveryDurableBoundary_ShouldCommitHistoryWithCurrentState(string route)
    {
        // Arrange: capture all writes/flushes/length changes, including both sides
        // of the catalog commit, rather than only a callback after publication.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Declare(false).Compile();
        var second = Declare(true).Compile();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("history", timeout.Token);
        if (route != "first")
        {
            await database.ApplySchemaAsync(first, timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync("INSERT INTO items (Id, Value) VALUES (1,42)", cancellationToken: timeout.Token);
            if (route == "rollover")
            {
                for (int index = 0; index < 31; index++)
                { await database.Catalog.SaveSchemaStateAsync(new(index.ToString("x64"), first.CanonicalDocument, database.Catalog.DdlGeneration), timeout.Token); }
                await database.Catalog.SaveSchemaStateAsync(new(first.Hash, first.CanonicalDocument, database.Catalog.DdlGeneration), timeout.Token);
            }
        }
        int previousEntries = database.Catalog.AppliedSchemaHistory.Length;
        database.Coordinator.Checkpoint(timeout.Token);
        database.CatalogStorage.Checkpoint();
        var recording = storage.RecordCrashPoints();

        // Act
        using (recording)
        {
            if (route == "save")
            { await database.Catalog.SaveSchemaStateAsync(new(second.Hash, second.CanonicalDocument, database.Catalog.DdlGeneration), timeout.Token); }
            else
            {
                await database.SchemaProvisioner.ApplyAsync(second, timeout.Token, deferDataScalingSteps: route == "deferred");
                while (database.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, timeout.Token); }
            }
        }

        // Assert: recovery exposes exactly old+old history or new+new history,
        // never the pending target in history. Reopen every captured crash image.
        recording.Count.ShouldBeGreaterThan(2);
        int oldStates = 0, newStates = 0;
        for (int point = 0; point < recording.Count; point++)
        {
            await using var recovered = await Builder(recording.Open(point)).BuildAsync(timeout.Token);
            var open = await recovered.OpenDatabaseAsync("history", timeout.Token);
            var state = open.Catalog.SchemaState;
            bool published = state?.ContentHash == second.Hash;
            if (published) { newStates++; }
            else
            {
                oldStates++;
                if (route == "first") { state.ShouldBeNull(); }
                else { state!.ContentHash.ShouldBe(first.Hash); }
            }
            var history = open.Catalog.AppliedSchemaHistory;
            history.Length.ShouldBe(previousEntries + (published ? 1 : 0), $"{route} crash image {point}");
            if (route != "first") { history.ShouldContain(value => value.ContentHash == first.Hash); }
            open.Catalog.HasAppliedSchema(second.Hash).ShouldBe(published);
            if (state is not null)
            { history.Single(value => value.ContentHash == state.ContentHash).LastAppliedGeneration.ShouldBe(state.Generation); }
            if (route != "save")
            {
                bool exists = open.Catalog.TryGetTable("dbo", "items", out var table);
                exists.ShouldBe(route != "first" || published);
                if (exists)
                {
                    (table!.FindColumn("Added") is not null).ShouldBe(published);
                    await using var reader = await open.CreateSessionAsync(timeout.Token);
                    var rows = await RowsAsync(reader, "SELECT Id, Value FROM items", timeout.Token);
                    if (route == "first") { rows.ShouldBeEmpty(); }
                    else { rows.Single().ShouldBe([1, 42]); }
                }
            }
            open.Catalog.PendingMigration.ShouldBeNull();
        }
        oldStates.ShouldBeGreaterThan(0);
        newStates.ShouldBeGreaterThan(0);
        _output.WriteLine($"{route}: reopened all {recording.Count} durable images; {oldStates} old states, {newStates} published states; no split history/current state.");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - TYPE rollback: lossless widening applies; narrowing holds the newer values")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_TypeRollback_ShouldApplyOnlyLosslessReversal(bool forwardNarrowing)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool wide, bool optIn) => SqlSchema.Create("history", schema =>
        {
            if (optIn) { schema.AllowDestructiveChanges(); }
            if (wide) { schema.Table<WideRow>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); }); }
            else { schema.Table<Row>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); }); }
        });
        var first = Version(forwardNarrowing, false);
        var second = Version(!forwardNarrowing, true);
        await using (var seed = await Builder(storage).BuildAsync(timeout.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", timeout.Token);
            await database.ApplySchemaAsync(first.Compile(), timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES (1,42)", cancellationToken: timeout.Token);
            await database.ApplySchemaAsync(second.Compile(), timeout.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act
        await using var rollback = await Builder(storage, first).BuildAsync(timeout.Token);
        var restored = await rollback.OpenDatabaseAsync("history", timeout.Token);

        // Assert
        restored.Catalog.SchemaState!.ContentHash.ShouldBe(forwardNarrowing ? first.Compile().Hash : second.Compile().Hash);
        if (forwardNarrowing) { restored.Provisioning.ShouldBeNull(); }
        else { restored.Provisioning!.FailureCode.ShouldBe("CS03405"); restored.Provisioning.Detail!.ShouldContain("AlterColumn 'items.Value'", Case.Sensitive); }
        await using var reader = await restored.CreateSessionAsync(timeout.Token);
        Convert.ToInt64((await RowsAsync(reader, "SELECT Value FROM items", timeout.Token)).Single()[0]).ShouldBe(42);
        // Redeploy the later declaration: narrowing is a roll-forward with its own opt-in.
        await using var forward = await Builder(storage.CaptureDurableImages(), second).BuildAsync(timeout.Token);
        var current = await forward.OpenDatabaseAsync("history", timeout.Token);
        current.Provisioning.ShouldBeNull();
        current.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
        await using var forwardReader = await current.CreateSessionAsync(timeout.Token);
        Convert.ToInt64((await RowsAsync(forwardReader, "SELECT Value FROM items", timeout.Token)).Single()[0]).ShouldBe(42);
    }

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage, SqlSchema? schema = null, bool inline = true)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("history");
        builder.Options.StorageStrategy = storage;
        if (schema is not null) { builder.AddDatabase("history", database => { database.Schema(schema); database.RunMigrationsInline = inline; }); }
        return builder;
    }

    private static SqlSchema Declare(bool extra, bool optIn = false, bool index = false, bool required = false, bool withDefault = false)
        => SqlSchema.Create("history", schema =>
        {
            if (optIn) { schema.AllowDestructiveChanges(); }
            if (!required)
            {
                schema.Table<Row>("items", table =>
                {
                    table.Key(row => row.Id);
                    table.Column(row => row.Value);
                    if (extra) { table.Column(row => row.Added); }
                    if (index || extra) { table.Index(row => row.Value); }
                });
            }
            else
            {
                schema.Table<RequiredRow>("items", table =>
                {
                    table.Key(row => row.Id);
                    table.Column(row => row.Value);
                    if (withDefault) { table.Default(row => row.Added, 7); }
                    else { table.Column(row => row.Added); }
                });
            }
        });

    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken token)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: token);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }

    private sealed record Row(int Id, int Value, int? Added);
    private sealed record WideRow(int Id, long Value);
    private sealed record RequiredRow(int Id, int Value, int Added);
}
