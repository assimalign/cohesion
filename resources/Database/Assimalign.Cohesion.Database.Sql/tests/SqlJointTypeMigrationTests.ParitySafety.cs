using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record ParityTypedRow(int Id, decimal? Value, int? Legacy);
    private sealed record ParityTypedShortRow(int Id, decimal? Value);

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint parity: a domain-only Build publishes a zero-executable-step schema and reopens idempotently")]
    public async Task BuildAsync_ParityDomainOnly_ShouldPublishMarkerAndReopen()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var declaration = SqlSchema.Create("typed", schema => schema.Type<Amount>(type => type.Decimal(8, 2)));
        await using (var seed = await Builder(storage).AddDatabase(declaration).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            // Act / Assert
            database.Catalog.Tables.ShouldBeEmpty();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(declaration.Compile().Hash);
            database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
            database.Catalog.PendingMigration.ShouldBeNull();
            storage = storage.CaptureDurableImages();
        }
        await using var reopened = await Builder(storage).AddDatabase(declaration).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        current.SchemaProvisioner.JointMetrics.ShouldBeNull();
        current.Catalog.SchemaState!.ContentHash.ShouldBe(declaration.Compile().Hash);
        current.IsOffline.ShouldBeFalse();
    }

    private static SqlSchema ParityDeclaration(string kind, bool target, bool index = false) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        if (kind == "drop" && target || kind == "missing" && !target)
        {
            schema.Table<ParityTypedShortRow>("items", table =>
            { table.Key(row => row.Id); table.Column(row => row.Value, "DECIMAL(10,0)"); if (index) { table.Index(row => row.Id); } });
        }
        else
        {
            schema.Table<ParityTypedRow>("items", table =>
            {
                table.Key(row => row.Id);
                table.Column(row => row.Value, kind == "precision-tighten" && target ? "DECIMAL(5,0)" : "DECIMAL(10,0)");
                table.Column(row => row.Legacy);
                if (kind == "default") { table.DefaultSql(row => row.Value, target ? "2" : "1"); }
                if (kind == "missing" && target) { table.Default(row => row.Legacy, 7); }
                if (index) { table.Index(row => row.Id); }
            });
        }
    });

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: nullable checked-domain missing values validate once without visiting the keyed heap")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_ParityNullableDomainAppend_ShouldKeepHeapWithoutScanning(bool withDefault)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var builder = Builder(new());
        builder.Types.Add(SqlType.CreateDomain("positive", SqlType.Integer,
            SqlScalarFunction.Create<int?, bool>("positive_value", value => value is null || value > 0, SqlFunctionVolatility.Immutable)));
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = KeepParitySchema("nullable", false, true);
        var ordinary = KeepParitySchema("nullable", true, true).Tables.Single();
        var target = Compiled(new CompiledSchemaTable(ordinary.Name, ordinary.RowType,
            ordinary.Columns.Select(column => column.Name == "added" ? column with { CustomType = "positive", DefaultLiteral = withDefault ? "7" : null } : column).ToArray(),
            ordinary.PrimaryKey, ordinary.Indexes, ordinary.Constraints));
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,1,42,'ok',9)", cancellationToken: guard.Token);
        ulong owner = Table(database).ObjectId;
        int decoded = 0;
        SqlPlanExecutor.RowDecoded = _ => decoded++;
        try
        {
            // Act
            await database.ApplySchemaAsync(target, guard.Token);
            // Assert
            decoded.ShouldBe(0);
            Table(database).ObjectId.ShouldBe(owner);
            (await RowsAsync(writer, "SELECT added FROM items", guard.Token)).Single()[0].ShouldBe(withDefault ? 7 : null);
        }
        finally { SqlPlanExecutor.RowDecoded = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: invalid nullable-domain missing defaults retain the empty/tombstoned exception")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ApplySchemaAsync_ParityInvalidDomainMissing_ShouldMatchOrdinaryEmptyTableRule(int population)
    {
        // Arrange: zero rows, one live row, or only a committed tombstone.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (bool joint in new[] { false, true })
        {
            var builder = Builder(new());
            builder.Types.Add(SqlType.CreateDomain("positive", SqlType.Integer,
                SqlScalarFunction.Create<int?, bool>("positive_value", value => value is null || value > 0, SqlFunctionVolatility.Immutable)));
            await using var engine = await builder.BuildAsync(guard.Token);
            var database = await engine.CreateDatabaseAsync("typed", guard.Token);
            var source = KeepParitySchema("nullable", false, true);
            var shape = KeepParitySchema("nullable", true, true).Tables.Single();
            var target = Compiled(new CompiledSchemaTable(shape.Name, shape.RowType,
                shape.Columns.Select(column => column.Name == "added" ? column with { CustomType = "positive", DefaultLiteral = "-1" } : column).ToArray(),
                shape.PrimaryKey, shape.Indexes, shape.Constraints));
            await database.ApplySchemaAsync(source, guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            if (population != 0) { await writer.ExecuteAsync("INSERT INTO items VALUES(1,1,42,'ok',9)", cancellationToken: guard.Token); }
            if (population == 2) { await writer.ExecuteAsync("DELETE FROM items", cancellationToken: guard.Token); }
            async Task Apply()
            {
                if (joint) { await database.ApplySchemaAsync(target, guard.Token); }
                else { await ApplyOrdinaryParityAsync(database, source, target, guard.Token); }
            }
            // Act / Assert
            if (population == 1)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(Apply);
                if (joint) { failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402"); }
                database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
            }
            else
            {
                await Apply();
                database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            }
            database.IsOffline.ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: inline success, exception and cancellation retain admission until disposal can join cleanup")]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ApplySchemaAsync_ParityInlineCleanupRace_ShouldKeepGateAndJoinBeforeClosing(string outcome)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        using var release = new ManualResetEventSlim();
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = ParityDeclaration("default", false).Compile();
        var target = ParityDeclaration("default", true).Compile();
        await database.ApplySchemaAsync(source, guard.Token);
        int cleanups = 0;
        database.SchemaProvisioner.JointPreparationFinishing = () =>
        { Interlocked.Increment(ref cleanups); finishing.TrySetResult(); release.Wait(guard.Token); };
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase != "Validated") { return; }
            if (outcome == "failure") { throw new InvalidOperationException("injected inline cleanup race"); }
            if (outcome == "cancel") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        };
        var applying = Task.Run(async () => await database.ApplySchemaAsync(target, cancellation.Token), guard.Token);
        Task? disposing = null;
        int refusals = 0;
        try
        {
            await finishing.Task.WaitAsync(guard.Token);
            var elapsed = Stopwatch.StartNew();
            do
            {
                await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await database.ApplySchemaAsync(source, guard.Token));
                refusals++;
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || refusals < 10);
            // Act: disposal sees and joins the still-live preparation lease.
            disposing = database.DisposeAsync().AsTask();
            disposing.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); }
        if (outcome == "success") { await applying; }
        else if (outcome == "cancel") { await Should.ThrowAsync<OperationCanceledException>(async () => await applying); }
        else { await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await applying); }
        await disposing!.WaitAsync(guard.Token);
        // Assert
        refusals.ShouldBeGreaterThanOrEqualTo(10);
        cleanups.ShouldBe(1);
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        current.Catalog.SchemaState!.ContentHash.ShouldBe(outcome == "success" ? target.Hash : source.Hash);
        current.Catalog.PendingMigration.ShouldBeNull();
        await current.ApplySchemaAsync(outcome == "success" ? target : source, guard.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: script indexes and equal declared names survive new kept layouts once")]
    [InlineData("drop", false)]
    [InlineData("drop", true)]
    [InlineData("precision-tighten", false)]
    [InlineData("precision-tighten", true)]
    [InlineData("default", false)]
    [InlineData("default", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    public async Task ApplyAsync_ParityKeepScriptIndex_ShouldPreserveOneRegistration(string kind, bool declareSameName)
    {
        // Arrange: preserve the existing promotion precedence; this only adds new keep shapes.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).AddDatabase("typed", database =>
        { database.Schema(ParityDeclaration(kind, false)); database.Script("001", "CREATE INDEX IX_items_Id ON items(Id)"); }).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        ulong owner = Table(database).ObjectId;
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync(kind == "missing" ? "INSERT INTO items VALUES(1,42)" : "INSERT INTO items VALUES(1,42,9)", cancellationToken: guard.Token);
        // Act
        await database.SchemaProvisioner.ApplyAsync(ParityDeclaration(kind, true, declareSameName).Compile(), guard.Token);
        // Assert
        Table(database).ObjectId.ShouldBe(owner);
        database.Catalog.GetIndexes(owner).Count(index => index.Name == "IX_items_Id").ShouldBe(1);
        DurableIndexRows(database, "IX_items_Id").ShouldBe(1);
        database.IndexManager.ExportRegistrations().Count(index => index.ObjectId == owner && index.Definition.Name == "IX_items_Id").ShouldBe(1);
        (await RowsAsync(writer, "SELECT Id FROM items", guard.Token)).Single()[0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint parity: precision validation sees a violating writer committed before reservation")]
    public async Task ApplySchemaAsync_ParityPrecisionPriorWriter_ShouldValidateItsCommittedRowAndKeepOldSchema()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = ParityDeclaration("precision-tighten", false).Compile();
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await using var transaction = await writer.BeginTransactionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,123456,9)", cancellationToken: guard.Token);
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waited.TrySetResult(); } };
        var applying = database.ApplySchemaAsync(ParityDeclaration("precision-tighten", true).Compile(), guard.Token).AsTask();
        await waited.Task.WaitAsync(guard.Token);
        // Act
        await transaction.CommitAsync(guard.Token);
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await applying);
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        database.IsOffline.ShouldBeFalse();
        (await RowsAsync(writer, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(123456m);
        await database.ApplySchemaAsync(source, guard.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint parity: defaults preserve frozen missing values and drop/re-add never reuses the dropped component")]
    public async Task ApplySchemaAsync_ParityDefaultDropAndReadd_ShouldKeepFrozenValuesAndPhysicalOrdinals()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = ParityDeclaration("missing", false).Compile();
        var added = ParityDeclaration("missing", true).Compile();
        SqlCompiledSchema Default(int value) => Compiled(added.Tables.Select(table => new CompiledSchemaTable(table.Name, table.RowType,
            table.Columns.Select(column => column.Name == "Legacy" ? column with { DefaultLiteral = value.ToString(System.Globalization.CultureInfo.InvariantCulture) } : column).ToArray(),
            table.PrimaryKey, table.Indexes, table.Constraints)).ToArray());
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,42)", cancellationToken: guard.Token);
        ulong owner = Table(database).ObjectId;
        // Act: insert defaults change independently of the old row's missing value.
        await database.ApplySchemaAsync(added, guard.Token);
        await database.ApplySchemaAsync(Default(9), guard.Token);
        await writer.ExecuteAsync("INSERT INTO items(Id,Value) VALUES(2,42)", cancellationToken: guard.Token);
        // Assert
        (await RowsAsync(writer, "SELECT Legacy FROM items ORDER BY Id", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { 7, 9 });
        database.SchemaProvisioner.JointStage = phase => { if (phase == "Publishing") { throw new InvalidOperationException("injected drop refusal"); } };
        (await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(source, guard.Token))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        (await RowsAsync(writer, "SELECT Legacy FROM items ORDER BY Id", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { 7, 9 });
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(source, guard.Token);
        Table(database).DroppedColumnOrdinals.ShouldBe(new[] { 2 });
        await database.ApplySchemaAsync(Default(11), guard.Token);
        Table(database).ObjectId.ShouldBe(owner);
        Table(database).GetPhysicalOrdinal(2).ShouldBe(3);
        Table(database).DroppedColumnOrdinals.ShouldBe(new[] { 2 });
        (await RowsAsync(writer, "SELECT Legacy FROM items ORDER BY Id", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] { 11, 11 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: kept layouts admit at the measured need and refuse one page under")]
    [InlineData("nullable")]
    [InlineData("missing")]
    [InlineData("required")]
    [InlineData("drop")]
    [InlineData("widen")]
    [InlineData("precision-tighten")]
    [InlineData("default")]
    public async Task ApplySchemaAsync_ParityKeepExactAdmission_ShouldUseExistingSpaceLedger(string kind)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var source = KeepParitySchema(kind, false, true);
        var target = KeepParitySchema(kind, true, true);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(source, guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,1,42,'ok',9)", cancellationToken: guard.Token);
            database.Coordinator.Checkpoint(guard.Token);
            database.CatalogStorage.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        long need = 0;
        foreach (long shortage in new long[] { -1, 0, Page.Size })
        {
            var image = storage.CaptureDurableImages();
            await using var engine = await Builder(image).BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            if (shortage >= 0) { image.VolumeCapacityBytes = image.AllocatedBytes + need - shortage; }
            // Act / Assert
            if (shortage == Page.Size)
            {
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, guard.Token));
                failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
                database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
            }
            else
            {
                await database.ApplySchemaAsync(target, guard.Token);
                if (shortage == -1) { need = database.SchemaProvisioner.JointSpaceNeed; }
                database.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
                database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            }
            database.IsOffline.ShouldBeFalse();
            database.Catalog.PendingMigration.ShouldBeNull();
        }
        _output.WriteLine($"Exact keep admission {kind}: need={need}; one-page-under={need - Page.Size}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: empty Build, inline Build and Apply converge at every kept-layout storage boundary")]
    [InlineData("drop", 0)]
    [InlineData("drop", 1)]
    [InlineData("drop", 2)]
    [InlineData("precision-tighten", 0)]
    [InlineData("precision-tighten", 1)]
    [InlineData("precision-tighten", 2)]
    [InlineData("default", 0)]
    [InlineData("default", 1)]
    [InlineData("default", 2)]
    [InlineData("missing", 2)]
    public async Task OpenAsync_ParityInlineKeepCrashImages_ShouldConvergeToExactOldOrNew(string kind, int mode)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var source = ParityDeclaration(kind, false);
        var target = ParityDeclaration(kind, true);
        var storage = new CrashCaptureSqlStorageStrategy();
        ulong owner;
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(source.Compile(), guard.Token);
            owner = Table(database).ObjectId;
            if (mode != 0)
            {
                await using var writer = await database.CreateSessionAsync(guard.Token);
                await writer.ExecuteAsync(kind == "missing" ? "INSERT INTO items VALUES(1,42)" : "INSERT INTO items VALUES(1,42,9)", cancellationToken: guard.Token);
            }
            database.Coordinator.Checkpoint(guard.Token);
            database.CatalogStorage.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        CrashCaptureSqlStorageStrategy.CrashPointRecorder? recorder = null;
        var phases = new HashSet<string>();
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = phase =>
            {
                if (phase == "Planning") { recorder ??= storage.RecordCrashPoints(); }
                phases.Add(phase);
            };
            return new RecordingWorker(engine);
        });
        if (mode != 2) { builder.AddDatabase("typed", database => { database.Schema(target); database.RunMigrationsInline = mode == 1; }); }
        // Act
        await using (var migrated = await builder.BuildAsync(guard.Token))
        {
            var database = await migrated.OpenDatabaseAsync("typed", guard.Token);
            if (mode == 2) { await database.ApplySchemaAsync(target.Compile(), guard.Token); }
            database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
            recorder!.Dispose();
        }
        // Assert: open without a declaration observes the exact durable outcome.
        phases.ShouldContain("IntentDurable");
        phases.ShouldContain("Publishing");
        phases.ShouldContain("CatalogPublished");
        recorder.ShouldNotBeNull().Count.ShouldBeGreaterThan(1);
        for (int point = 0; point < recorder.Count; point++)
        {
            var image = recorder.Open(point);
            await using var reopened = await Builder(image).BuildAsync(guard.Token);
            var database = await reopened.OpenDatabaseAsync("typed", guard.Token);
            bool published = database.Catalog.SchemaState!.ContentHash == target.Compile().Hash;
            database.Catalog.SchemaState.ContentHash.ShouldBe(published ? target.Compile().Hash : source.Compile().Hash);
            Table(database).ObjectId.ShouldBe(owner);
            database.Catalog.PendingMigration.ShouldBeNull();
            await using var reader = await database.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(mode == 0 ? 0L : 1L);
            if (kind == "drop" && !published && mode != 0)
            { (await RowsAsync(reader, "SELECT Legacy FROM items", guard.Token)).Single()[0].ShouldBe(9); }
            if (kind == "precision-tighten") { Table(database).FindColumn("Value")!.Type.Precision.ShouldBe(published ? 5 : 10); }
            if (kind == "default") { Table(database).FindColumn("Value")!.DefaultLiteral.ShouldBe(published ? "2" : "1"); }
            // A rolled-back Build only reopens unpublished work; rollback after a
            // published DROP COLUMN is SQ-A7, not this migration's recovery rule.
            await using var declared = await Builder(recorder.Open(point)).AddDatabase(published ? target : source).BuildAsync(guard.Token);
            var current = await declared.OpenDatabaseAsync("typed", guard.Token);
            current.Catalog.SchemaState!.ContentHash.ShouldBe(published ? target.Compile().Hash : source.Compile().Hash);
        }
        _output.WriteLine($"Inline keep crash {kind}, mode={mode}: images={recorder.Count}; reopens={recorder.Count * 2}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint parity: kept DROP and precision enforce old-or-new constraints against racing writers")]
    [InlineData("drop", false)]
    [InlineData("drop", true)]
    [InlineData("precision-tighten", false)]
    [InlineData("precision-tighten", true)]
    public async Task ApplySchemaAsync_ParityKeepConcurrentWriter_ShouldRefuseNewViolationsOrKeepOldOnFailure(string kind, bool fail)
    {
        // Arrange: this class's collection disables parallelization. Counters, not
        // the minimum traffic duration, decide whether the reservation race occurred.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = ParityDeclaration(kind, false).Compile();
        var target = ParityDeclaration(kind, true).Compile();
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await using var unrelated = await database.CreateSessionAsync(guard.Token);
        await unrelated.ExecuteAsync("CREATE TABLE traffic(id INT)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase != "Validated") { return; }
            entered.TrySetResult();
            release.Wait(guard.Token);
            if (fail) { throw new InvalidOperationException("injected keep publication refusal"); }
        };
        int waits = 0;
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { Interlocked.Increment(ref waits); } };
        var applying = Task.Run(async () => await database.ApplySchemaAsync(target, guard.Token), guard.Token);
        Task<Execution.DatabaseResult>? writing = null;
        int traffic = 0;
        try
        {
            await entered.Task.WaitAsync(guard.Token);
            writing = writer.ExecuteAsync(kind == "drop" ? "INSERT INTO items(Id,Value,Legacy) VALUES(1,42,9)" : "INSERT INTO items VALUES(1,123456,9)", cancellationToken: guard.Token).AsTask();
            var elapsed = Stopwatch.StartNew();
            do
            {
                await unrelated.ExecuteAsync("INSERT INTO traffic VALUES(1)", cancellationToken: guard.Token);
                traffic++;
                writing.IsCompleted.ShouldBeFalse();
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || Volatile.Read(ref waits) == 0 || traffic < 10);
        }
        finally { release.Set(); }
        // Act / Assert
        if (fail)
        {
            (await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await applying)).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
            await writing!;
            database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        }
        else
        {
            await applying;
            await Should.ThrowAsync<DatabaseException>(async () => await writing!);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        }
        waits.ShouldBeGreaterThan(0);
        traffic.ShouldBeGreaterThanOrEqualTo(10);
        database.IsOffline.ShouldBeFalse();
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(fail ? source : target, guard.Token);
    }
}
