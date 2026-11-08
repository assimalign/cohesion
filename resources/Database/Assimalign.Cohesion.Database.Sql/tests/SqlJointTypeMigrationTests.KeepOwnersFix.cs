using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record CrashKeptRow(int V, int W, int T);
    private sealed record CrashNarrowRow(int V);
    private sealed record CrashWideRow(long V);

    private static SqlSchema CrashOwnerDeclaration(int version) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        if (version == 1) { schema.Table<CrashWideRow>("rewrite", table => table.Column(row => row.V)); }
        else { schema.Table<CrashNarrowRow>("rewrite", table => table.Column(row => row.V)); }
        schema.Table<CrashKeptRow>("items", table =>
        {
            table.Column(row => row.V); table.Column(row => row.W); table.Column(row => row.T);
            if (version == 1) { table.Index(row => row.W); }
            if (version == 2) { table.Index(row => row.V); table.Index(row => row.T); }
        });
    });

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint native domain changes convert stored rows before publishing NOT NULL")]
    [InlineData("varchar")]
    [InlineData("storage")]
    [InlineData("decimal")]
    [InlineData("char")]
    public async Task ApplyAsync_NativeDomainAndNotNull_ShouldRewriteAndConvertRows(string kind)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var oldType = kind switch
        {
            "storage" => SqlType.SmallInt,
            "decimal" => SqlType.Decimal(8, 3),
            "char" => SqlType.Char(8),
            _ => SqlType.VarChar(8),
        };
        var newType = kind switch
        {
            "storage" => SqlType.Integer,
            "decimal" => SqlType.Decimal(8, 1),
            "char" => SqlType.Char(4),
            _ => SqlType.VarChar(4),
        };
        var storage = new CrashCaptureSqlStorageStrategy();
        var source = Compiled(new CompiledSchemaTable("items", "items", [new("v", oldType.Storage.Type, true, CustomType: "native_value")], null, [], []));
        var target = Compiled(new CompiledSchemaTable("items", "items", [new("v", newType.Storage.Type, false, CustomType: "native_value")], null, [], []));
        var seedBuilder = Builder(storage);
        seedBuilder.Types.Add(SqlType.CreateDomain("native_value", oldType));
        ulong previous;
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(source, guard.Token);
            previous = Table(database).ObjectId;
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync(kind switch
            {
                "storage" => "INSERT INTO items VALUES(123)",
                "decimal" => "INSERT INTO items VALUES(1.256)",
                _ => "INSERT INTO items VALUES('ab      ')",
            }, cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        builder.Types.Add(SqlType.CreateDomain("native_value", newType));
        await using var engine = await builder.BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);
        string? classification = null;
        current.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "IntentDurable")
            { classification = current.Catalog.PendingMigration!.Steps.Single(step => step.Table == "items" && step.Kind is "Joint/KeepOwner" or "Joint/RewriteOwner").Kind; }
        };

        // Act
        if (kind == "storage")
        {
            // Isolate the classifier from portable operations: the stored domain base
            // differs while the operation describes only the NOT NULL transition.
            var portableSource = Compiled(new CompiledSchemaTable("items", "items",
                [new("v", newType.Storage.Type, true, CustomType: "native_value")], null, [], []));
            var script = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(portableSource, target), portableSource);
            var executor = new SqlPlanExecutor(current.DataStorage, current.Catalog, current.IndexManager, current.Definitions);
            var plan = executor.PlanJointMigration(target, script, guard.Token);
            classification = plan.Tables.Single().KeepsOwner ? "Joint/KeepOwner" : "Joint/RewriteOwner";
            var transaction = await current.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
            try
            {
                await current.Coordinator.LockManager.AcquireAsync(transaction.Sequence,
                    LockResource.Object(previous), LockMode.Exclusive, guard.Token);
                var statement = new SqlStatementContext(transaction, current.Coordinator, "typed", "typed", checkpointCopy: true);
                statement.ConfigureCopySpace((_, _, _, _) => { }, _ => { }, current.TableReclamation.CaptureAllocationCount);
                await executor.ExecuteJointMigrationAsync(plan, target, statement, _ => { }, guard.Token);
            }
            finally { await current.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
        }
        else { await current.ApplySchemaAsync(target, guard.Token); }

        // Assert
        await using var reader = await current.CreateSessionAsync(guard.Token);
        object expected = kind switch { "storage" => 123, "decimal" => 1.3m, _ => "ab  " };
        (await RowsAsync(reader, "SELECT v FROM items", guard.Token)).Single()[0].ShouldBe(expected);
        classification.ShouldBe("Joint/RewriteOwner");
        Table(current).ObjectId.ShouldNotBe(previous);
        Table(current).Columns[0].Type.ShouldBe(newType.Storage);
        Table(current).Columns[0].IsNullable.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint index drops and replacements keep the heap and reclaim only their tree")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_IndexRemoval_ShouldKeepHeapAndReclaimTree(bool replace)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declaration(bool changed, IReadOnlyList<CompiledSchemaIndex>? indexes = null)
            => Compiled(OwnerDeclaration(changed, indexes).Tables.Select(table => table.Name != "items" ? table : new CompiledSchemaTable(
                table.Name, table.RowType, table.Columns.Append(new CompiledSchemaColumn("padding", DatabaseType.String, true, MaxLength: 1000)).ToArray(),
                table.PrimaryKey, table.Indexes, table.Constraints)).ToArray());
        await database.ApplySchemaAsync(Declaration(false), guard.Token);
        SeedRaw(database, [7, 8, new string('x', 1000)], 1024);
        // Ordinary DDL and first-Build trees use their own physical owners too.
        await ApplyOrdinaryParityAsync(database, Declaration(false), Declaration(false, [new("ix_v", ["w"], false)]), guard.Token);
        ulong heap = Table(database).ObjectId;
        var oldTree = database.IndexManager.ExportRegistrations().Single();
        ulong treeOwner = database.IndexManager.PageOwner(oldTree);
        treeOwner.ShouldNotBe(heap);
        IndexOwnerPages(database, treeOwner).ShouldBeGreaterThan(1);
        var heapPages = database.DataStorage.GetOwnerPages(heap).ToArray();
        long rewritten = 0;
        SqlPlanExecutor.RewriteStage = (stage, count) => { if (stage == "Copied") { rewritten += count; } };
        string? classification = null;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "IntentDurable")
            { classification = database.Catalog.PendingMigration!.Steps.Single(step => step.Table == "items" && step.Kind is "Joint/KeepOwner" or "Joint/RewriteOwner").Kind; }
        };

        // Act
        try
        {
            await database.ApplySchemaAsync(Declaration(true, replace ? [new("ix_v", ["v"], false)] : []), guard.Token);
            await DrainReclamationAsync(database, guard.Token);
        }
        finally { SqlPlanExecutor.RewriteStage = null; }

        // Assert
        IndexOwnerPages(database, treeOwner).ShouldBe(0);
        IndexOwnerPages(database, heap).ShouldBe(0);
        database.DataStorage.GetOwnerPages(heap).ShouldBe(heapPages);
        classification.ShouldBe("Joint/KeepOwner");
        Table(database).ObjectId.ShouldBe(heap);
        rewritten.ShouldBe(0);
        database.SchemaProvisioner.JointSpaceReplacementBytes.ShouldBeLessThan(heapPages.LongLength * 8192 / 10);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE v=7", guard.Token)).Single()[0].ShouldBe(1024L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint CHECK and NOT NULL refusal happens before any tree build")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_KeptRowConstraintFailure_ShouldBuildNoTrees(bool notNull)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync(notNull ? "INSERT INTO items VALUES(NULL,1)" : "INSERT INTO items VALUES(7,1)", cancellationToken: guard.Token);
        int builds = 0;
        database.SchemaProvisioner.JointStage = stage => { if (stage == "BeforeTreeBuild") { builds++; } };

        // Act
        await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(
            OwnerDeclaration(true, constraints: notNull ? [] : [new("ck", CompiledSchemaConstraintKind.Check, [], null, [], new("v < 3"))], nullable: !notNull), guard.Token));

        // Assert
        builds.ShouldBe(0);
        database.OfflineError.ShouldBeNull();
        database.Catalog.PendingMigration.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint publication enforces the kept physical layout under its catalog lock")]
    public async Task PublishStorageReplacementAsync_ReorderedKeptColumns_ShouldRefuseWithoutPublication()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        var old = Table(database);
        var reordered = new SqlCatalogTable(old.ObjectId, old.Schema, old.Name, old.Columns.Reverse().ToArray(),
            old.PrimaryKeyColumns, old.Owner, old.OwningSchema, old.Constraints, old.DroppedColumnOrdinals);

        // Act
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await database.Catalog.PublishStorageReplacementAsync(
            [], null, [], [], database.IndexManager.ExportRegistrations(), new(), false, TransactionSequence.None, string.Empty,
            null, guard.Token, kept: [reordered], expectedKept: [old]));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05060");
        failure.Message.ShouldContain("Kept table 'dbo.items' must preserve existing physical column names, ordinals and dropped ordinals", Case.Sensitive);
        Table(database).ShouldBeSameAs(old);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A refused joint publication preserves the old same-name tree and the join fast path")]
    public async Task ApplyAsync_ReplacementPublicationSpaceRefusal_ShouldKeepOldTreeAndReadFastPath()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO rewrite VALUES(1)", cancellationToken: guard.Token);
        var source = OwnerDeclaration(true, [new("ix_v", ["v"], true)]);
        await database.ApplySchemaAsync(source, guard.Token);
        await DrainReclamationAsync(database, guard.Token);
        var old = Table(database);
        var registration = database.IndexManager.ExportRegistrations().Single();
        database.IndexManager.TryGetIndex(old.ObjectId, "ix_v", out var oldTree).ShouldBeTrue();
        database.Catalog.IsTruncatedTable(old).ShouldBeFalse();
        int publications = 0;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "Publishing") { publications++; storage.MigrationAvailableBytes = 0; }
        };

        // Act: the watchdog runs after PreparePublication and before the catalog bracket.
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(
            OwnerDeclaration(false, [new("ix_v", ["w"], false)]), guard.Token));
        storage.MigrationAvailableBytes = long.MaxValue;
        await DrainReclamationAsync(database, guard.Token);

        // Assert
        failure.Message.ShouldContain("0 bytes available");
        publications.ShouldBe(1);
        Table(database).ShouldBeSameAs(old);
        database.IndexManager.TryGetIndex(old.ObjectId, "ix_v", out var retained).ShouldBeTrue();
        retained.ShouldBeSameAs(oldTree);
        database.IndexManager.ExportRegistrations().ShouldBe([registration]);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(source.Hash);
        (await RowsAsync(writer, "SELECT i.w,r.v FROM items i CROSS JOIN rewrite r WHERE i.v=7", guard.Token)).Single().ShouldBe(new object?[] { 8, 1L });
        var retry = await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync("INSERT INTO items VALUES(7,9)", cancellationToken: guard.Token));
        retry.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03037");
        database.Catalog.IsTruncatedTable(old).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - A writer bound before joint publication aborts CS04006 then retries against the new constraints")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_StaleKeptWriter_ShouldAbortThenEnforceNewConstraint(bool unique)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = stage => { if (stage == "Validated") { ready.TrySetResult(); release.Wait(guard.Token); } };
        var target = OwnerDeclaration(true, unique ? [new("uq", ["v"], true)] : [],
            unique ? [] : [new("ck", CompiledSchemaConstraintKind.Check, [], null, [], new("v < 3"))]);
        string invalid = unique ? "INSERT INTO items VALUES(1,2)" : "INSERT INTO items VALUES(9,2)";
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        var statement = new SqlStatementContext(transaction, database.Coordinator, databaseName: "typed");
        var planner = new SqlPlanner(database.Catalog, null, database.Definitions.Functions, guard.Token, statement);
        var plan = planner.Plan(SqlQueryRequest.FromSql(invalid).Statement.SqlExpression);
        var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
        var applying = Task.Run(async () => await database.ApplySchemaAsync(target, guard.Token), guard.Token);
        await ready.Task.WaitAsync(guard.Token);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        try
        {
            // Act
            var write = executor.ExecuteAsync(plan, statement, guard.Token);
            await waiting.Task.WaitAsync(guard.Token);
            release.Set();
            await applying;
            var stale = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await write);

            // Assert
            stale.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS04006");
            var retry = await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync(invalid, cancellationToken: guard.Token));
            retry.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(unique ? "CS03037" : "CS03038");
            await writer.ExecuteAsync("INSERT INTO items VALUES(2,2)", cancellationToken: guard.Token);
        }
        finally { release.Set(); await applying; await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint progress and events count executable steps without classification records")]
    public async Task ApplyAsync_JointClassification_ShouldExcludePseudoStepsFromProgressAndEvents()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        int expected = 2, observed = 0;
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "OwnersDurable") { return; }
            observed++;
            database.Provisioning!.StepCount.ShouldBe(expected);
            database.Catalog.PendingMigration!.Steps.Count.ShouldBe(expected + 2);
        };

        // Act
        await database.ApplySchemaAsync(OwnerDeclaration(true), guard.Token);

        // Assert
        observed.ShouldBe(1);
        var events = recorder.Events.Where(entry => entry.EventName is "DeferredStepBound" or "DeferredStepProgress" or "DeferredStepCompleted").ToArray();
        events.ShouldNotBeEmpty();
        foreach (var entry in events) { entry.Payload![6].ShouldBe(expected); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Alternate UNIQUE trees serialize concurrent duplicate inserts by logical key")]
    public async Task ExecuteAsync_AlternateUniqueConcurrentDuplicates_ShouldExerciseConflictsAndKeepOneRowPerKey()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(true, [new("uq", ["v"], true)]), guard.Token);
        var registration = database.IndexManager.ExportRegistrations().Single();
        database.IndexManager.PageOwner(registration).ShouldNotBe(Table(database).ObjectId);
        int successes = 0, conflicts = 0, rounds = 0, waits = 0;
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { Interlocked.Increment(ref waits); } };
        var elapsed = Stopwatch.StartNew();

        // Act: minimum exercise interval, then counter completion, bounded by guard.
        do
        {
            int key = ++rounds;
            int arrivals = 0;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await using var writer = await database.CreateSessionAsync(guard.Token);
                if (Interlocked.Increment(ref arrivals) == 4) { ready.TrySetResult(); }
                await start.Task.WaitAsync(guard.Token);
                try
                {
                    await writer.ExecuteAsync($"INSERT INTO items VALUES({key},1)", cancellationToken: guard.Token);
                    Interlocked.Increment(ref successes);
                }
                catch (DatabaseException failure) when (failure.Diagnostics.Any(diagnostic => diagnostic.Code == "CS03037"))
                { Interlocked.Increment(ref conflicts); }
            }, guard.Token)).ToArray();
            await ready.Task.WaitAsync(guard.Token);
            start.TrySetResult();
            await Task.WhenAll(tasks);
        } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || successes < 8 || conflicts < 24 || Volatile.Read(ref waits) < 8);

        // Assert
        successes.ShouldBe(rounds);
        conflicts.ShouldBe(3 * rounds);
        successes.ShouldBeGreaterThanOrEqualTo(8);
        conflicts.ShouldBeGreaterThanOrEqualTo(24);
        waits.ShouldBeGreaterThanOrEqualTo(8);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe((long)rounds);
        _output.WriteLine($"Alternate UNIQUE stress: rounds={rounds}; successes={successes}; conflicts={conflicts}; waits={waits}");
    }
}
