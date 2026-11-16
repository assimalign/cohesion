using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record ReviewFullRow(int Id, int Legacy, int Neighbour, decimal Value);
    private sealed record ReviewDroppedRow(int Id, int Neighbour, decimal Value);
    private sealed record ReviewAppendRow(int Id, int Value, int? Added);

    private static SqlSchema ReviewDropDeclaration(bool dropColumn, bool target) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        if (dropColumn && target)
        {
            schema.Table<ReviewDroppedRow>("items", table =>
            { table.Key(row => row.Id); table.Column(row => row.Neighbour); table.Column(row => row.Value, "DECIMAL(10,0)"); table.Index(row => row.Value); });
        }
        else
        {
            schema.Table<ReviewFullRow>("items", table =>
            {
                table.Key(row => row.Id); table.Column(row => row.Legacy); table.Column(row => row.Neighbour); table.Column(row => row.Value, "DECIMAL(10,0)");
                table.Index(row => row.Value); if (!target) { table.Index(row => row.Legacy); }
            });
        }
    });

    [Theory(DisplayName = "Cohesion Test [Sql] - Review fix: deferred Build keeps first-Build heap-owned drops at exact admission and every crash boundary (#1778)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_ReviewHeapDrop_ShouldKeepRowsPagesAndReclaimOnce(bool dropColumn, bool retireByType)
    {
        // Arrange: the declared secondary trees, including the removed one, share the heap.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var source = ReviewDropDeclaration(dropColumn, false);
        var target = ReviewDropDeclaration(dropColumn, true);
        var storage = new CrashCaptureSqlStorageStrategy();
        ulong owner; long treePages; long droppedRoot;
        await using (var seed = await Builder(storage).AddDatabase(source).BuildAsync(guard.Token))
        {
            var db = await seed.OpenDatabaseAsync("typed", guard.Token);
            owner = Table(db).ObjectId;
            var removed = db.IndexManager.ExportRegistrations().Single(index => index.Definition.Name.Contains("Legacy", StringComparison.Ordinal));
            db.IndexManager.PageOwner(removed).ShouldBe(owner);
            droppedRoot = removed.RootPageId;
            await using var writer = await db.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(1, 128).Select(id => $"({id},99,{id + 10},{id})")), cancellationToken: guard.Token);
            treePages = IndexOwnerPages(db, owner);
            treePages.ShouldBe(3); // Three one-page trees, so the removed root is one stranded page.
            db.Coordinator.Checkpoint(guard.Token); db.CatalogStorage.Checkpoint();
            storage = storage.CaptureDurableImages();
        }
        long need = 0;
        for (int admission = 0; admission < 3; admission++)
        {
            var image = storage.CaptureDurableImages();
            var boundaries = new List<(string Stage, CrashCaptureSqlStorageStrategy Image)>();
            var builder = Builder(image);
            int copies = 0; long rewritten = 0; long decoded = 0;
            SqlPlanExecutor.RowDecoded = _ => decoded++;
            SqlPlanExecutor.RewriteStage = (stage, rows) => { if (stage == "Copied") { copies++; rewritten += rows; } };
            builder.AddWorker(engine =>
            {
                engine.BeforeProvisioning = db =>
                {
                    db.SchemaProvisioner.JointStage = stage =>
                    {
                    if (stage == "Planning" && admission != 0) { image.VolumeCapacityBytes = image.AllocatedBytes + need - (admission == 2 ? Page.Size : 0); }
                    if (stage == "IntentDurable") { db.Catalog.PendingMigration!.Steps.ShouldNotContain(step => step.Kind == "Joint/RewriteOwner"); }
                    boundaries.Add((stage, image.CaptureDurableImages()));
                    };
                };
                return new RecordingWorker(engine);
            });
            try
            {
                // Act
                if (admission == 2)
                {
                    var refusal = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.AddDatabase(target).BuildAsync(guard.Token));
                    refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
                    refusal.Message.ShouldContain("joint-migrations"); refusal.Message.ShouldContain("typed"); refusal.Message.ShouldContain("bytes needed");
                    continue;
                }
                await using var engine = await builder.AddDatabase(target).BuildAsync(guard.Token);
                var db = await engine.OpenDatabaseAsync("typed", guard.Token);
                // Assert: Build itself completed the cheap job, without visiting a row.
                db.SchemaProvisioner.Deferred.ShouldBeNull();
                copies.ShouldBe(0); rewritten.ShouldBe(0); decoded.ShouldBe(0);
                db.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
                if (admission == 0) { need = db.SchemaProvisioner.JointSpaceNeed; }
                db.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
                Table(db).ObjectId.ShouldBe(owner);
                await DrainReclamationAsync(db, guard.Token);
                IndexOwnerPages(db, owner).ShouldBe(treePages);
                (IndexOwnerPages(db, owner) - db.Catalog.GetIndexes(owner).Count).ShouldBe(1);
                db.DataStorage.FreeSpaceMap.IsAllocated((PageId)droppedRoot).ShouldBeTrue();
                using (var page = db.DataStorage.PageManager.GetPage((PageId)droppedRoot)) { page.Page.OwnerId.ShouldBe(owner); }
                await ReviewAssertRowsAsync(db, dropColumn, guard.Token);
                if (admission == 0)
                {
                    foreach (var boundary in boundaries)
                    {
                        await using var recovered = await Builder(boundary.Image).BuildAsync(guard.Token);
                        var current = await recovered.OpenDatabaseAsync("typed", guard.Token);
                        await ReviewAssertRowsAsync(current, dropColumn && current.Catalog.SchemaState!.ContentHash == target.Compile().Hash, guard.Token);
                    }
                    SqlPlanExecutor.RewriteStage = null; SqlPlanExecutor.RowDecoded = null;
                    await using var copyEngine = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
                    var copyDb = await copyEngine.OpenDatabaseAsync("typed", guard.Token);
                    copyDb.SchemaProvisioner.ForceJointRewrite = true;
                    await copyDb.ApplySchemaAsync(target.Compile(), guard.Token);
                    long copyNeed = copyDb.SchemaProvisioner.JointSpaceNeed;
                    copyNeed.ShouldBeGreaterThan(need);
                    _output.WriteLine($"copy alternative need={copyNeed}; keep admission has no room for this copy");
                    _output.WriteLine($"heap drop column={dropColumn}, type retirement={retireByType}: need={need}, boundaries={boundaries.Count}, stranded pages=1, root={droppedRoot}, retained index pages={treePages}; rows rewritten=0; commits=1");
                    continue;
                }
                await using (var reopened = await Builder(image.CaptureDurableImages()).AddDatabase(target).BuildAsync(guard.Token))
                { await ReviewAssertRowsAsync(await reopened.OpenDatabaseAsync("typed", guard.Token), dropColumn, guard.Token); }
                // Retiring the heap must free the stranded tree too, once and only once.
                image.VolumeCapacityBytes = long.MaxValue;
                db.SchemaProvisioner.JointStage = null;
                var compiled = target.Compile();
                var retirement = retireByType ? Compiled(compiled.Tables.Select(table => new CompiledSchemaTable(table.Name, table.RowType,
                    table.Columns.Select(column => column.Name == "Value" ? column with { Type = DatabaseType.Int64, Precision = null, Scale = null, CustomType = null } : column).ToArray(),
                    table.PrimaryKey, table.Indexes, table.Constraints)).ToArray()) : Compiled();
                await db.SchemaProvisioner.ApplyAsync(retirement, guard.Token);
                await DrainReclamationAsync(db, guard.Token);
                IndexOwnerPages(db, owner).ShouldBe(0);
                db.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
                long free = db.DataStorage.PageManager.FreePageCount;
                await DrainReclamationAsync(db, guard.Token);
                db.DataStorage.PageManager.FreePageCount.ShouldBe(free);
            }
            finally { SqlPlanExecutor.RewriteStage = null; SqlPlanExecutor.RowDecoded = null; }
        }
    }

    private static async Task ReviewAssertRowsAsync(SqlDatabase db, bool dropped, CancellationToken token)
    {
        await using var reader = await db.CreateSessionAsync(token);
        await using (var result = (DatabaseResultSet)await reader.ExecuteAsync("SELECT * FROM items ORDER BY Id", cancellationToken: token))
        {
            result.Columns.Select(column => column.Name).ShouldBe(dropped ? new[] { "Id", "Neighbour", "Value" } : ["Id", "Legacy", "Neighbour", "Value"]);
            int count = 0;
            await foreach (var row in result.GetRowsAsync(token)) { row.GetValue(0).ShouldBe(++count); row.GetValue(dropped ? 2 : 3).ShouldBe((decimal)count); }
            count.ShouldBe(128);
        }
        (await RowsAsync(reader, "SELECT Id FROM items WHERE Value=42", token)).Single()[0].ShouldBe(42);
        reader.LastStatementMetrics!.AccessPath.ShouldBe("seek:IX_items_Value");
        (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE Neighbour>=11", token)).Single()[0].ShouldBe(128L);
        reader.LastStatementMetrics!.AccessPath.ShouldBe("scan");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Review fix: any populated replacement is handed to the worker before copying (#1778)")]
    public async Task BuildAsync_ReviewForcedReplacement_ShouldReturnBeforeCopy()
    {
        // Arrange: force a metadata append through the replacement oracle, outside TYPE/deferred-step classification.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = await SeedAsync(guard.Token);
        var builder = Builder(storage);
        ulong previousOwner = 0;
        long rewritten = 0;
        SqlPlanExecutor.RewriteStage = (stage, rows) => { if (stage == "Copied") { Interlocked.Exchange(ref rewritten, rows); } };
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = db =>
            {
                previousOwner = Table(db).ObjectId;
                db.SchemaProvisioner.ForceJointRewrite = true;
                db.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
            };
            return new RecordingWorker(engine);
        });
        var target = SqlSchema.Create("typed", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<ReviewAppendRow>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value, "INT"); table.DefaultSql(row => row.Value, "7"); table.Index(row => row.Value); table.Column(row => row.Added); });
            schema.Table<OtherRow>("other", table => table.Column(row => row.Id));
        });
        // Act / Assert
        try
        {
            await using var engine = await builder.AddDatabase(target).BuildAsync(guard.Token);
            var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            await entered.Task.WaitAsync(guard.Token);
            db.SchemaProvisioner.Deferred.ShouldNotBeNull();
            rewritten.ShouldBe(0);
            db.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
            release.Set(); await WaitDoneAsync(db, guard.Token);
            db.Catalog.SchemaState!.ContentHash.ShouldBe(target.Compile().Hash);
            rewritten.ShouldBe(65); Table(db).ObjectId.ShouldNotBe(previousOwner);
        }
        finally { release.Set(); SqlPlanExecutor.RewriteStage = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Review fix: imperative interruptions clear health; deferred interruptions remain sticky (#1778)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_ReviewInterruption_ShouldDistinguishImperativeAndDeferred(bool deferred)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var db = await engine.OpenDatabaseAsync("typed", guard.Token);
        db.SchemaProvisioner.BeforeDeferredStep = (_, _) => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); };
        // Act
        if (deferred) { await db.SchemaProvisioner.ApplyAsync(Target().Compile(), cancel.Token, true); await WaitDoneAsync(db, guard.Token); }
        else { await Should.ThrowAsync<OperationCanceledException>(async () => await db.ApplySchemaAsync(Target().Compile(), cancel.Token)); }
        // Assert
        if (deferred) { db.Provisioning!.FailureCode.ShouldBe("CS03402"); }
        else { db.Provisioning.ShouldBeNull(); }
        db.IsOffline.ShouldBeFalse(); db.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Review fix: an ad-hoc incoming FK gives a coded dependency refusal for a declared column drop (#1778)")]
    public async Task BuildAsync_ReviewIncomingForeignKeyDrop_ShouldRefuseWithConstraintAndKeepOldSchema()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var shape = ReviewDropDeclaration(true, false).Compile().Tables.Single();
        var source = Compiled(new CompiledSchemaTable(shape.Name, shape.RowType, shape.Columns, shape.PrimaryKey,
            shape.Indexes.Select(index => index.Columns.Contains("Legacy") ? index with { IsUnique = true } : index).ToArray(), shape.Constraints));
        await database.ApplySchemaAsync(source, guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1,7,11,42)", cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE TABLE child(code INT, CONSTRAINT fk_review FOREIGN KEY(code) REFERENCES items(Legacy))", cancellationToken: guard.Token);
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(ReviewDropDeclaration(true, true).Compile(), guard.Token));
        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03105");
        failure.Message.ShouldContain("fk_review");
        failure.InnerException!.InnerException.ShouldBeOfType<SqlExecutionException>().Diagnostics.ShouldContain(diagnostic => diagnostic.Code == SqlExecutionException.DependentObjectsCode);
        Table(database).FindColumn("Legacy").ShouldNotBeNull(); database.IsOffline.ShouldBeFalse();
        (await RowsAsync(session, "SELECT Legacy FROM items", guard.Token)).Single()[0].ShouldBe(7);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Review fix: SET NOT NULL ignores unrelated registered domains and JSON validation (#1778)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ApplyAsync_ReviewChangedColumnsOnly_ShouldSkipUnrelatedValidation(bool tightenDomain, bool unrelatedDefault)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        bool strict = false; int domainCalls = 0;
        var builder = Builder(new());
        builder.Types.Add(SqlType.CreateDomain("review_positive", SqlType.Integer,
            SqlScalarFunction.Create<int?, bool>("review_check", value => { domainCalls++; return !strict || value > 0; }, SqlFunctionVolatility.Immutable)));
        await using var engine = await builder.BuildAsync(guard.Token);
        var db = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declaration(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            [new("id", DatabaseType.Int32, false), new("flag", DatabaseType.Int32, !target), new("unrelated", DatabaseType.Int32, true, CustomType: "review_positive", DefaultLiteral: unrelatedDefault ? "-1" : null), new("document", DatabaseType.Json, true)], new("pk", ["id"]), [], []));
        await db.ApplySchemaAsync(Declaration(false), guard.Token);
        await using var session = await db.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1,1,-1,'{\"large\":\"" + new string('x', 8000) + "\"}')", cancellationToken: guard.Token);
        strict = tightenDomain; domainCalls = 0; int jsonCalls = 0;
        SqlJsonAccess.ValidationObserved = () => jsonCalls++;
        try
        {
            // Act
            await db.ApplySchemaAsync(Declaration(true), guard.Token);
            // Assert
            _output.WriteLine($"unrelated validation: JSON={jsonCalls}, domain={domainCalls}");
            jsonCalls.ShouldBe(0); domainCalls.ShouldBe(0);
            Table(db).FindColumn("flag")!.IsNullable.ShouldBeFalse();
            db.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        }
        finally { SqlJsonAccess.ValidationObserved = null; }
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - Review fix: rejected handoffs report the producer outcome rather than cleanup cancellation (#1778)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApplyAsync_ReviewHandoffOutcome_ShouldReportOriginalProducerFailure(bool deferred, bool cancelled)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var db = await engine.OpenDatabaseAsync("typed", guard.Token);
        db.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "Handoff") { return; }
            if (!cancelled) { throw new InvalidOperationException("injected producer failure"); }
            cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested();
        };
        // Act
        async Task Apply() => await db.SchemaProvisioner.ApplyAsync(Target().Compile(), cancel.Token, deferred);
        if (cancelled) { await Should.ThrowAsync<OperationCanceledException>(Apply); }
        else { (await Should.ThrowAsync<SqlSchemaMigrationException>(Apply)).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402"); }
        // Assert
        if (deferred || !cancelled) { db.Provisioning!.FailureCode.ShouldBe("CS03402"); }
        else { db.Provisioning.ShouldBeNull(); }
        db.SchemaProvisioner.Deferred.ShouldBeNull(); db.Catalog.PendingMigration.ShouldBeNull();
        db.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash); db.IsOffline.ShouldBeFalse();
        await using var session = await db.CreateSessionAsync(guard.Token);
        (await RowsAsync(session, "SELECT Value FROM items WHERE Id=1", guard.Token)).Single()[0].ShouldBe(1);
        await session.ExecuteAsync("CREATE VIEW after_rejected_handoff AS SELECT Id FROM items", cancellationToken: guard.Token);
        db.SchemaProvisioner.JointStage = null;
        await db.ApplySchemaAsync(Source().Compile(), guard.Token); db.Provisioning.ShouldBeNull();
    }

}
