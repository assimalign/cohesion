using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint migration preserves the whole-life lock matrix (#1778)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ApplyAsync_WholeLifeConcurrency_ShouldPreserveLockMatrix(int mode)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var source = NeighbourDeclaration(); var baseTarget = NeighbourDeclaration(mode == 2 ? "parentRewrite" : "index", mode == 1);
        SqlCompiledSchema AddMirror(SqlCompiledSchema schema, bool target) => Compiled(schema.Tables.Concat([new CompiledSchemaTable("mirror", "mirror",
            [new("id", DatabaseType.Int32, false), new("note", target && mode == 2 ? DatabaseType.Int64 : DatabaseType.Int32, false)], new("pk", ["id"]), [], [])])
            .Concat(target ? [new CompiledSchemaTable("created", "created", [new("id", DatabaseType.Int32, false)], new("pk", ["id"]), [], [])] : Array.Empty<CompiledSchemaTable>()).ToArray());
        source = AddMirror(source, false); var target = AddMirror(baseTarget, true);
        await using var engine = await Builder(new()).BuildAsync(guard.Token); var db = await engine.CreateDatabaseAsync("typed", guard.Token);
        await db.ApplySchemaAsync(source, guard.Token);
        await using (var seed = await db.CreateSessionAsync(guard.Token))
        { foreach (string sql in new[] { "INSERT INTO customers VALUES(1,7),(4,7),(5,7)", "INSERT INTO orders VALUES(1,1),(3,1),(4,1),(5,1),(6,1)", "INSERT INTO alternate VALUES(1,8)", "INSERT INTO mirror VALUES(1,7)" }) { await seed.ExecuteAsync(sql, cancellationToken: guard.Token); } }
        using var stages = new BlockingCollection<string>(); using var release = new SemaphoreSlim(0);
        string[] watched = ["IntentDurable", "LocksReserved", "Handoff", "OwnersDurable", "TreesDurable", "BeforeTreeBuild", "AfterTreeBuild", "TablePrepared", "Validated", "Publishing", "CatalogPublished", "Published"];
        int stop = 0, stagesSeen = 0, half = 0; long freeOps = 0, reads = 0; var errors = new ConcurrentQueue<string>();
        var waits = new ConcurrentDictionary<long, bool>(); var freeTransactions = new ConcurrentDictionary<long, bool>();
        using var listener = new LockWaitListener(db.Name);
        listener.Observed = e => { if (e.EventName == "LockWaitStart") { waits[(long)e.Payload![1]!] = true; } if (e.EventName?.Contains("Deadlock", StringComparison.OrdinalIgnoreCase) == true) { errors.Enqueue("deadlock"); } };
        db.SchemaProvisioner.JointStage = stage =>
        {
            if (!watched.Contains(stage)) { return; }
            var snapshot = db.Catalog.CaptureSnapshot();
            bool created = snapshot.Tables.Any(t => t.Name == "created");
            bool published = mode == 2 ? snapshot.Tables.Single(t => t.Name == "customers").FindColumn("note")!.Type.Type == DatabaseType.Int64 :
                snapshot.GetIndexes(snapshot.Tables.Single(t => t.Name == (mode == 0 ? "orders" : "customers")).ObjectId).Any(i => i.Name == (mode == 0 ? "ix_customer" : "ix_note"));
            if (created != published) { Interlocked.Increment(ref half); }
            if (mode == 2)
            { bool a = snapshot.Tables.Single(t => t.Name == "customers").FindColumn("note")!.Type.Type == DatabaseType.Int64; bool b = snapshot.Tables.Single(t => t.Name == "mirror").FindColumn("note")!.Type.Type == DatabaseType.Int64; if (a != b) { Interlocked.Increment(ref half); } }
            stages.Add(stage); release.Wait(guard.Token);
        };
        async Task FreeActor(string sql, bool read)
        {
            await using var s = await db.CreateSessionAsync(guard.Token);
            while (Volatile.Read(ref stop) == 0)
            {
                try
                {
                    // Explicit transactions identify waits caused by permitted actors.
                    await s.ExecuteAsync("BEGIN", cancellationToken: guard.Token);
                    var transaction = s.CurrentTransaction!; freeTransactions[(long)transaction.Context.Sequence.Value] = true;
                    if (read) { await RowsAsync(s, sql, guard.Token); Interlocked.Increment(ref reads); }
                    else { await s.ExecuteAsync(sql, cancellationToken: guard.Token); }
                    await s.ExecuteAsync("COMMIT", cancellationToken: guard.Token); Interlocked.Increment(ref freeOps);
                    await Task.Delay(1, guard.Token);
                }
                catch (Exception e) { errors.Enqueue(e.Message); break; }
            }
        }
        async Task<Exception?> OneActor(string sql)
        {
            await using var s = await db.CreateSessionAsync(guard.Token);
            return await Record.ExceptionAsync(async () => { if (sql.StartsWith("SELECT", StringComparison.Ordinal)) { var result = await RowsAsync(s, sql, guard.Token); if (sql.Contains("JOIN", StringComparison.Ordinal)) { foreach (var row in result) { if (row[0]!.GetType() != row[1]!.GetType()) { Interlocked.Increment(ref half); } } } } else { await s.ExecuteAsync(sql, cancellationToken: guard.Token); } });
        }
        var actors = new List<Task> { FreeActor("UPDATE alternate SET note=note+1 WHERE id=1", false), FreeActor("SELECT * FROM alternate", true) };
        // Mirror is rewritten in mode 2, so its reader is a waiter instead.
        if (mode != 2)
        {
            actors.Add(FreeActor("SELECT * FROM mirror", true));
            string stable = mode == 0 ? "customers" : "orders";
            actors.Add(FreeActor("SELECT * FROM " + stable, true));
            if (mode == 0) { actors.Add(FreeActor("UPDATE customers SET note=note+1 WHERE id=4", false)); actors.Add(FreeActor("UPDATE customers SET note=s.note FROM alternate AS s WHERE customers.id=5 AND s.id=1", false)); }
        }
        // Act: keep actors alive through every recorded stop and at least one second.
        var lifetime = Stopwatch.StartNew();
        var job = Task.Run(async () => { await db.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true); await WaitDoneAsync(db, guard.Token); });
        var waiting = new List<Task<Exception?>>(); Task<Exception?>? createdReader = null;
        try
        {
            while (!job.IsCompleted)
            {
                if (!stages.TryTake(out var stage, 100)) { continue; }
                stagesSeen++;
                if (stage == "IntentDurable")
                {
                    waiting.Add(OneActor("SELECT * FROM orders")); waiting.Add(OneActor("SELECT * FROM customers"));
                    waiting.Add(OneActor("INSERT INTO orders VALUES(2,1)")); waiting.Add(OneActor("DELETE FROM customers WHERE id=1"));
                    if (mode == 0) { waiting.Add(OneActor("INSERT INTO customers VALUES(3,9)")); waiting.Add(OneActor("UPDATE customers SET id=6 WHERE id=1")); }
                    if (mode == 1)
                    {
                        waiting.Add(OneActor("UPDATE orders SET id=id WHERE id=3")); waiting.Add(OneActor("DELETE FROM orders WHERE id=4"));
                        waiting.Add(OneActor("MERGE INTO orders USING (SELECT 5 AS id,1 AS customer_id) AS s ON orders.id=s.id WHEN MATCHED THEN UPDATE SET customer_id=s.customer_id"));
                        waiting.Add(OneActor("INSERT INTO orders VALUES(6,1) ON CONFLICT(id) DO UPDATE SET customer_id=EXCLUDED.customer_id"));
                    }
                    if (mode == 2) { waiting.Add(OneActor("SELECT customers.note,mirror.note FROM customers JOIN mirror ON customers.id=mirror.id")); }
                }
                if (stage == "Handoff") { createdReader = OneActor("SELECT * FROM created"); waiting.Add(createdReader); }
                if (stage == "Published")
                {
                    while (lifetime.Elapsed < TimeSpan.FromSeconds(1) || Volatile.Read(ref freeOps) == 0 || Volatile.Read(ref reads) == 0)
                    { await Task.Delay(1, guard.Token); }
                }
                release.Release();
            }
            await job; await Task.WhenAll(waiting);
        }
        finally
        {
            Volatile.Write(ref stop, 1); release.Release(32); db.SchemaProvisioner.JointStage = null;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await Task.WhenAll(actors).WaitAsync(cleanup.Token);
        }
        int unexpected = waits.Keys.Count(t => freeTransactions.ContainsKey(t));
        _output.WriteLine($"concurrency seed=177804 mode={mode} stages={stagesSeen} freeOps={freeOps} reads={reads} waits={waits.Count} unexpected={unexpected} half={half} errors={string.Join('|',errors)} waiters={waiting.Count}");
        // Assert
        (await createdReader!).ShouldBeNull();
        var waiterResults = await Task.WhenAll(waiting);
        waiterResults.ShouldNotContain(e => e is DatabaseParseException);
        waiterResults.ShouldNotContain(e => e != null && e.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase));
        errors.ShouldBeEmpty(); half.ShouldBe(0); unexpected.ShouldBe(0); waits.ShouldNotBeEmpty(); freeOps.ShouldBeGreaterThan(0); reads.ShouldBeGreaterThan(0); stagesSeen.ShouldBeGreaterThanOrEqualTo(11); db.Provisioning.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Script index promotion remains writable through restarts (#1778)")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task ApplyAsync_ScriptIndexPromotion_ShouldRemainWritableThroughRestarts(bool unique, bool rewrite, bool stale)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var storage = new CrashCaptureSqlStorageStrategy();
        string script = $"CREATE {(unique ? "UNIQUE " : "")}INDEX IX_items_Part ON items(Part)" + (stale ? "; DROP INDEX IX_items_Part ON items" : "");
        await using (var seed = await Builder(storage).AddDatabase("typed", d => { d.Schema(PromotionSchema(false)); d.Script("001", script); }).BuildAsync(guard.Token))
        {
            var db = await seed.OpenDatabaseAsync("typed", guard.Token); await using var s = await db.CreateSessionAsync(guard.Token);
            await s.ExecuteAsync("INSERT INTO items VALUES(1,7)", cancellationToken: guard.Token);
            _output.WriteLine($"promotion unique={unique} rewrite={rewrite} stale={stale} release=N rows={DurableIndexRows(db,"IX_items_Part")}");
            storage = storage.CaptureDurableImages();
        }
        for (int release = 1; release <= 4; release++)
        {
            // Act
            bool declared = release <= 2;
            // An added index makes the removal release a deferred plan as well.
            await using var engine = await Builder(storage).AddDatabase("typed", d => d.Schema(PromotionSchema(declared, rewrite, release >= 3))).BuildAsync(guard.Token);
            var db = await engine.OpenDatabaseAsync("typed", guard.Token); await WaitDoneAsync(db, guard.Token);
            var failure = db.Provisioning?.FailureCode; int count = DurableIndexRows(db, "IX_items_Part");
            await using var s = await db.CreateSessionAsync(guard.Token);
            var write = await Record.ExceptionAsync(async () => await s.ExecuteAsync($"INSERT INTO items VALUES({release+1},7)", cancellationToken: guard.Token));
            _output.WriteLine($"promotion unique={unique} rewrite={rewrite} stale={stale} release={release} rows={count} failure={failure} write={write}");
            // Assert
            failure.ShouldBeNull(); db.IsOffline.ShouldBeFalse(); count.ShouldBe(declared ? 1 : 0); write.ShouldBeNull(); storage = storage.CaptureDurableImages();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Narrowed script keys preserve the old schema on duplicate (#1778)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_NarrowedScriptKey_ShouldKeepOldSchemaAfterDuplicate(bool unique)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SqlCompiledSchema Declare(bool narrow) => Compiled(new CompiledSchemaTable("items", "row", [new("Id", narrow ? DatabaseType.Float32 : DatabaseType.Float64, false)], new("pk", ["Id"]), [], []));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        { var db = await seed.CreateDatabaseAsync("typed", guard.Token); await db.ApplySchemaAsync(Declare(false), guard.Token); storage = storage.CaptureDurableImages(); }
        await using (var seed = await Builder(storage).AddDatabase("typed", d => d.Script("001", $"CREATE {(unique ? "UNIQUE " : "")}INDEX script_key ON items(Id)")).BuildAsync(guard.Token))
        {
            var db = await seed.OpenDatabaseAsync("typed", guard.Token); await using var s = await db.CreateSessionAsync(guard.Token);
            await s.ExecuteAsync("INSERT INTO items VALUES(16777216),(16777217)", cancellationToken: guard.Token); storage = storage.CaptureDurableImages();
        }
        for (int restart = 0; restart < 3; restart++)
        {
            await using var engine = await Builder(storage).BuildAsync(guard.Token); var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            Exception? failure = null;
            // Act
            if (restart == 0) { failure = await Record.ExceptionAsync(async () => await db.ApplySchemaAsync(Declare(true), guard.Token)); }
            int count = DurableIndexRows(db, "script_key");
            await using var s = await db.CreateSessionAsync(guard.Token);
            var write = await Record.ExceptionAsync(async () => await s.ExecuteAsync($"INSERT INTO items VALUES({100+restart})", cancellationToken: guard.Token));
            _output.WriteLine($"narrow unique={unique} restart={restart} scriptRows={count} type={Table(db).Columns[0].Type.Type} failure={failure?.Message ?? db.Provisioning?.FailureCode} base={failure?.GetBaseException().Message} write={write}");
            // Assert
            if (restart == 0) { failure.ShouldNotBeNull(); db.Provisioning!.FailureCode.ShouldBe("CS03402"); failure.GetBaseException().Message.ShouldContain("duplicate key"); }
            Table(db).Columns[0].Type.Type.ShouldBe(DatabaseType.Float64);
            db.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(false).Hash);
            db.Catalog.Scripts.ShouldHaveSingleItem().Name.ShouldBe("001");
            db.IsOffline.ShouldBeFalse(); count.ShouldBe(1); write.ShouldBeNull();
            storage = storage.CaptureDurableImages();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Existing unique index order survives joint publication and restart (#1778)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ApplyAsync_ExistingUniqueIndexOrder_ShouldMatchPerStepThroughRestart(int shape)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            shape == 3
                ? target ? [new("Id", DatabaseType.Int32, false), new("Added", DatabaseType.Int32, false, DefaultLiteral: "9")] : [new("Id", DatabaseType.Int32, false)]
                : shape == 2
                    ? [new("Id", DatabaseType.Int32, false), new("Value", target ? DatabaseType.Int64 : DatabaseType.Int32, false)]
                    : [new("Id", target && shape == 1 ? DatabaseType.Int64 : DatabaseType.Int32, false)],
            null, target ? [new("ix_added", ["Id"], true)] : [], []));
        var image = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(image).BuildAsync(guard.Token))
        {
            var db = await seed.CreateDatabaseAsync("typed", guard.Token); await db.ApplySchemaAsync(Declare(false), guard.Token);
            await using var s = await db.CreateSessionAsync(guard.Token);
            await s.ExecuteAsync("CREATE UNIQUE INDEX adhoc_key ON items(Id)", cancellationToken: guard.Token);
            await s.ExecuteAsync(shape == 2 ? "INSERT INTO items VALUES(1,1)" : "INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            db.Coordinator.Checkpoint(guard.Token); image = image.CaptureDurableImages();
        }
        var messages = new string?[2];
        for (int mode = 0; mode < 2; mode++)
        {
            var storage = image.CaptureDurableImages();
            await using var engine = await Builder(storage).BuildAsync(guard.Token);
            var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            // Act
            if (mode == 0) { await ApplyPerStepAsync(db, Declare(false), Declare(true), guard.Token); }
            else { await db.SchemaProvisioner.ApplyAsync(Declare(true), guard.Token, deferDataScalingSteps: true); await WaitDoneAsync(db, guard.Token); }
            await using var s = await db.CreateSessionAsync(guard.Token);
            messages[mode] = (await Record.ExceptionAsync(async () => await s.ExecuteAsync(shape == 2 ? "INSERT INTO items VALUES(1,1)" : "INSERT INTO items(Id) VALUES(1)", cancellationToken: guard.Token)))?.Message;
            // Assert
            messages[mode].ShouldNotBeNull(); messages[mode]!.ShouldContain("adhoc_key");
            var names = db.Catalog.GetIndexes(Table(db).ObjectId).Select(i => i.Name).ToArray();
            names.ShouldBe(new[] { "adhoc_key", "ix_added" });
            await using var reopenedEngine = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
            var reopened = await reopenedEngine.OpenDatabaseAsync("typed", guard.Token);
            reopened.Catalog.GetIndexes(Table(reopened).ObjectId).Select(i => i.Name).ShouldBe(names);
            await using var reopenedSession = await reopened.CreateSessionAsync(guard.Token);
            (await Record.ExceptionAsync(async () => await reopenedSession.ExecuteAsync(shape == 2 ? "INSERT INTO items VALUES(1,1)" : "INSERT INTO items(Id) VALUES(1)", cancellationToken: guard.Token)))!.Message.ShouldBe(messages[mode]);
            _output.WriteLine($"order shape={shape} mode={mode} indexes={string.Join(',', db.Catalog.GetIndexes(Table(db).ObjectId).Select(i => i.Name))} message={messages[mode]}");
        }
        messages[1].ShouldBe(messages[0]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Interval checkpoints publish at exact sparse admission (#1778)")]
    public async Task ApplyAsync_BetweenBracketCheckpoint_ShouldPublishAtExactAdmission()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var source = WideCheckpointDeclaration(false); var target = WideCheckpointDeclaration(true);
        var image = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(image).BuildAsync(guard.Token))
        {
            var db = await seed.CreateDatabaseAsync("typed", guard.Token);
            await db.ApplySchemaAsync(source, guard.Token);
            SeedRaw(db, [1, new string('x', 4000)], 16384);
            db.Coordinator.Checkpoint(guard.Token);
            image = image.CaptureDurableImages();
        }
        long need = 0;
        for (int run = 0; run < 2; run++)
        {
            var storage = image.CaptureDurableImages(); storage.UnixSparseGrowth = true;
            var builder = Builder(storage);
            builder.Options.BufferPoolCapacity = 64L * 1024 * 1024;
            builder.Options.CheckpointJournalSize = 256L * 1024 * 1024;
            builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
            builder.Options.PageWriteBackInterval = TimeSpan.FromDays(1);
            await using var engine = await builder.BuildAsync(guard.Token);
            var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
            {
                db.Coordinator.Checkpoint(guard.Token);
                int checkpoints = 0, observed = 0, forced = 0; long copied = 0, peak = 0;
                var checkpointer = engine.Workers.OfType<SqlCheckpointWorker>().Single();
                db.SchemaProvisioner.JointStage = stage =>
                { if (stage == "OwnersDurable" && run == 1) { storage.VolumeCapacityBytes = storage.AllocatedBytes + need; } };
                SqlPlanExecutor.RewriteStage = (stage, value) =>
                {
                    if (stage == "CopyCheckpointObserved") { observed++; }
                    if (stage == "CopyCheckpointed") { forced++; }
                    if (stage != "Copied") { return; }
                    copied = value; peak = Math.Max(peak, db.DataStorage.JournalLength);
                    // Invoke the engine's actual interval-poll path between committed brackets.
                    if (value % 256 == 0 && PollCheckpoint(checkpointer, db, TimeSpan.FromTicks(1), guard.Token)) { checkpoints++; }
                };
                try
                {
                    // Act
                    var failure = await Record.ExceptionAsync(async () => await db.ApplySchemaAsync(target, guard.Token));
                    if (run == 0) { need = db.SchemaProvisioner.JointSpaceNeed; }
                    _output.WriteLine($"interval exact={run == 1}; need={need}; copied={copied}; peak={peak}; checkpoints={checkpoints}; observed={observed}; forced={forced}; capacityFailures={storage.CapacityFailures}; offline={db.IsOffline}; failure={failure}");
                    // Assert
                    failure.ShouldBeNull();
                    db.Provisioning.ShouldBeNull();
                    checkpoints.ShouldBe(64);
                    observed.ShouldBe(4);
                    forced.ShouldBe(0);
                    copied.ShouldBe(16384);
                    db.IsOffline.ShouldBeFalse();
                    db.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
                    storage.CapacityFailures.ShouldBe(0);
                }
                finally { storage.VolumeCapacityBytes = long.MaxValue; SqlPlanExecutor.RewriteStage = null; db.SchemaProvisioner.JointStage = null; }
            });
        }
    }

    private static async Task ApplyPerStepAsync(SqlDatabase database, SqlCompiledSchema source, SqlCompiledSchema target, CancellationToken token)
    {
        var script = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(source, target), source);
        await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
            script.Steps.Select((step, i) => new SqlCatalogMigrationStep(step.Kind, step.Operation.ParentName ?? step.Operation.ObjectName, step.Operation.ObjectName, i + 1)).ToArray(), script.Steps.Count), token);
        var locks = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            await using var session = database.CreateSchemaSession("typed", token, locks.Sequence);
            session.ProvisioningProgress = (_, _) => { };
            foreach (var step in script.Steps) { await session.ExecuteAsync(step.Request, token); }
            await database.Catalog.SaveSchemaStateAsync(new(target.Hash, target.CanonicalDocument, database.Catalog.DdlGeneration), token);
        }
        finally { await database.Coordinator.RollbackAsync(locks, CancellationToken.None); }
    }

    private static string DifferentialValue(object? value) => value is null ? "NULL" : value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);

    private static async Task<string[]> CaptureDifferentialSnapshotAsync(SqlDatabase database, CancellationToken token)
    {
        var state = new List<string>();
        await using var session = await database.CreateSessionAsync(token);
        foreach (var table in database.Catalog.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            state.Add("TABLE " + table.Name + " PK=" + string.Join(',', table.PrimaryKeyColumns));
            foreach (var column in table.Columns)
            { state.Add("COLUMN " + column.Name + " " + column.Type + " " + column.IsNullable + " " + column.DefaultLiteral); }
            foreach (var index in database.Catalog.GetIndexes(table.ObjectId).OrderBy(i => i.Name, StringComparer.Ordinal))
            { state.Add("INDEX " + index.Name + " " + string.Join(',', index.ColumnNames) + " " + index.IsUnique + " " + index.IsPrimaryKey + " " + index.Predicate + " " + string.Join(',', index.KeyOrders)); }
            foreach (var constraint in table.Constraints.OrderBy(c => c.Name, StringComparer.Ordinal))
            { state.Add("CONSTRAINT " + constraint.Name + " " + constraint.Kind + " " + string.Join(',', constraint.Columns) + " " + constraint.ReferencedTable + " " + string.Join(',', constraint.ReferencedColumns ?? []) + " " + constraint.CheckExpression + " " + constraint.OnDelete + " " + constraint.OnUpdate); }
            foreach (var row in await RowsAsync(session, $"SELECT * FROM {table.Name} ORDER BY Id,Part", token))
            { state.Add("ROW " + string.Join('|', row.Select(DifferentialValue))); }
        }
        foreach (var row in await RowsAsync(session, "SELECT TABLE_NAME,CONSTRAINT_NAME,CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS ORDER BY TABLE_NAME,CONSTRAINT_NAME", token))
        { state.Add("INFO " + string.Join('|', row.Select(DifferentialValue))); }
        return state.ToArray();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Seeded joint migration matches per-step behavior (#1778)")]
    public async Task ApplyAsync_SeededDifferential_ShouldMatchPerStep()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        const int seed = 177804;
        var random = new Random(seed);
        int keep = 0, rewrite = 0, keyedKeep = 0, both = 0;
        var differences = new List<string>();
        for (int pair = 0; pair < 96; pair++)
        {
            int keyShape = pair % 4;
            bool retype = random.Next(2) == 0;
            bool priorIndex = random.Next(3) == 0;
            bool dropIndex = priorIndex && random.Next(2) == 0;
            bool partial = random.Next(2) == 0;
            bool unique = random.Next(2) == 0;
            bool create = random.Next(2) == 0;
            bool fk = keyShape != 0 && random.Next(2) == 0;
            bool outgoing = fk && random.Next(2) == 0;
            bool adhoc = random.Next(3) == 0;
            bool append = random.Next(4) == 0;
            var key = keyShape == 0 ? null : new CompiledSchemaKey("pk", keyShape == 2 ? ["Id", "Part"] : ["Id"]);
            SqlCompiledSchema Declaration(bool target)
            {
                var columns = new List<CompiledSchemaColumn> { new("Id", DatabaseType.Int32, false), new("Part", DatabaseType.Int32, false), new("Value", target && retype ? DatabaseType.Int64 : DatabaseType.Int32, false) };
                if (target && append) { columns.Add(new("Added", DatabaseType.Int32, false, DefaultLiteral: "9")); }
                var indexes = new List<CompiledSchemaIndex>();
                if (keyShape == 3) { indexes.Add(new("ux_id", ["Id"], true)); }
                if (priorIndex && (!target || !dropIndex)) { indexes.Add(new("ix_old", ["Part"], false)); }
                if (target) { indexes.Add(new("ix_added", unique ? ["Id", "Part"] : ["Value"], unique, partial ? "Part >= 0" : null)); }
                var constraints = new List<CompiledSchemaConstraint>();
                if (outgoing) { constraints.Add(new("fk_items_parent", CompiledSchemaConstraintKind.Reference, ["Part"], "parent", ["Id"])); }
                var tables = new List<CompiledSchemaTable> { new("items", "itemsrow", columns, key, indexes, constraints) };
                tables.Add(new("parent", "parentrow", [new("Id", DatabaseType.Int32, false), new("Part", DatabaseType.Int32, false)], new("pk_parent", ["Id"]), target && fk ? [new("ix_p", ["Part"], false)] : [], []));
                if (fk && !outgoing)
                { tables.Add(new("child", "childrow", [new("Id", DatabaseType.Int32, false), new("Part", DatabaseType.Int32, false)], new("pk_child", ["Id"]), [], [new("fk_child", CompiledSchemaConstraintKind.Reference, keyShape == 2 ? ["Id", "Part"] : ["Id"], "items", keyShape == 2 ? ["Id", "Part"] : ["Id"])])); }
                if (target && create)
                { tables.Add(new("created", "createdrow", [new("Id", DatabaseType.Int32, false), new("Part", DatabaseType.Int32, false)], new("pk_created", ["Id", "Part"]), [], keyShape == 0 ? [] : [new("fk_created", CompiledSchemaConstraintKind.Reference, keyShape == 2 ? ["Id", "Part"] : ["Id"], "items", keyShape == 2 ? ["Id", "Part"] : ["Id"])])); }
                return Compiled(tables.ToArray());
            }
            var source = Declaration(false); var target = Declaration(true);
            var image = new CrashCaptureSqlStorageStrategy();
            await using (var engine = await Builder(image).BuildAsync(guard.Token))
            {
                var db = await engine.CreateDatabaseAsync("typed", guard.Token);
                await db.ApplySchemaAsync(source, guard.Token);
                await using var session = await db.CreateSessionAsync(guard.Token);
                await session.ExecuteAsync("INSERT INTO parent VALUES(0,0),(1,1),(2,2),(3,3)", cancellationToken: guard.Token);
                var order = Enumerable.Range(1, 16).ToArray(); random.Shuffle(order);
                await session.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', order.Select(i => $"({i},{i % 4},{i * 10})")), cancellationToken: guard.Token);
                if (fk && !outgoing) { await session.ExecuteAsync("INSERT INTO child VALUES(1,1),(2,2)", cancellationToken: guard.Token); }
                if (adhoc) { await session.ExecuteAsync("CREATE UNIQUE INDEX adhoc_key ON items(Id)", cancellationToken: guard.Token); }
                db.Coordinator.Checkpoint(guard.Token);
                image = image.CaptureDurableImages();
            }
            // Act
            var snapshots = new string[2][]; var errors = new string?[2];
            for (int mode = 0; mode < 2; mode++)
            {
                await using var engine = await Builder(image.CaptureDurableImages()).BuildAsync(guard.Token);
                var db = await engine.OpenDatabaseAsync("typed", guard.Token);
                db.SchemaProvisioner.JointStage = stage =>
                {
                    if (stage != "IntentDurable") { return; }
                    var steps = db.Catalog.PendingMigration!.Steps;
                    if (steps.Any(s => s.Table == "items" && s.Kind == "Joint/KeepOwner")) { keep++; if (keyShape != 0) { keyedKeep++; } }
                    if (steps.Any(s => s.Table == "items" && s.Kind == "Joint/RewriteOwner")) { rewrite++; }
                };
                var error = await Record.ExceptionAsync(async () =>
                {
                    if (mode == 0) { await ApplyPerStepAsync(db, source, target, guard.Token); }
                    else { await db.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true); await WaitDoneAsync(db, guard.Token); }
                });
                errors[mode] = error?.ToString() ?? db.Provisioning?.FailureCode;
                if (errors[mode] is null)
                {
                    var state = (await CaptureDifferentialSnapshotAsync(db, guard.Token)).ToList();
                    await using var session = await db.CreateSessionAsync(guard.Token);
                    foreach (var statement in new[] { "INSERT INTO items(Id,Part,Value) VALUES(1,1,99)", "UPDATE items SET Value=321 WHERE Id=2", "DELETE FROM items WHERE Id=1" })
                    {
                        var failure = await Record.ExceptionAsync(async () => await session.ExecuteAsync(statement, cancellationToken: guard.Token));
                        state.Add("DML " + statement + " = " + failure?.Message);
                    }
                    if (keyShape != 0)
                    {
                        string pk = db.Catalog.GetIndexes(Table(db).ObjectId).Single(i => i.IsPrimaryKey).Name;
                        var failure = await Record.ExceptionAsync(async () => await session.ExecuteAsync($"INSERT INTO items(Id,Part,Value) VALUES(3,3,88) ON CONFLICT ON CONSTRAINT {pk} DO UPDATE SET Value=EXCLUDED.Value", cancellationToken: guard.Token));
                        state.Add("CONFLICT=" + failure?.Message);
                    }
                    state.AddRange(await CaptureDifferentialSnapshotAsync(db, guard.Token));
                    snapshots[mode] = state.ToArray();
                }
            }
            if (errors.Any(e => e is not null))
            { differences.Add($"PAIR={pair} key={keyShape} retype={retype} prior={priorIndex} drop={dropIndex} partial={partial} unique={unique} create={create} fk={fk} outgoing={outgoing} adhoc={adhoc} append={append}\nPERSTEP={errors[0]}\nJOINT={errors[1]}"); }
            else
            {
                both++;
                if (!snapshots[0].SequenceEqual(snapshots[1]))
                { differences.Add($"PAIR={pair} key={keyShape} retype={retype} prior={priorIndex} drop={dropIndex} partial={partial} unique={unique} create={create} fk={fk} outgoing={outgoing} adhoc={adhoc} append={append}\nPERSTEP ONLY:\n" + string.Join('\n', snapshots[0].Except(snapshots[1])) + "\nJOINT ONLY:\n" + string.Join('\n', snapshots[1].Except(snapshots[0]))); }
            }
        }
        // Assert
        _output.WriteLine($"seed={seed}; pairs=96; both-success={both}; KeepOwner={keep}; keyedKeep={keyedKeep}; RewriteOwner={rewrite}; differences={differences.Count}");
        foreach (string difference in differences) { _output.WriteLine(difference); }
        both.ShouldBe(96);
        keep.ShouldBe(33); keyedKeep.ShouldBe(27); rewrite.ShouldBe(63);
        differences.ShouldBeEmpty();
    }

}
