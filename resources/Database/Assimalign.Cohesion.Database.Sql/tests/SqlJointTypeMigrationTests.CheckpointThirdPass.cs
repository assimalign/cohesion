using System;
using System.Linq;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private static SqlCompiledSchema WideCheckpointDeclaration(bool changed) => Compiled(new CompiledSchemaTable("items", "row",
        [new("Value", changed ? DatabaseType.Int64 : DatabaseType.Int32, false), new("Payload", DatabaseType.String, false, MaxLength: 4000)], null, [], []));

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint checkpoint: reclamation holds the gate, then copy checkpoints, including after a failed apply")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ApplyAsync_ReclamationBracketAtCheckpoint_ShouldWaitAtGateAndPublish(bool retry, bool cancel)
    {
        // Arrange: hold a real reclamation bracket fenced before inline apply starts.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        using var release = new ManualResetEventSlim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = WideCheckpointDeclaration(false);
        var target = WideCheckpointDeclaration(true);
        await database.ApplySchemaAsync(source, guard.Token);
        SeedRaw(database, [1, new string('x', 4000)], 4096);
        database.Coordinator.Checkpoint(guard.Token);
        int batches = 0, busy = 0, checkpoints = 0;
        long peak = 0;
        database.TableReclamation.BeforeBatchCommit = (_, token) =>
        { Interlocked.Increment(ref batches); entered.TrySetResult(); release.Wait(token); };
        storage.DurableStorageChanged = catalog =>
        { if (!catalog) { peak = Math.Max(peak, database.DataStorage.Journal.Length); } };
        try
        {
            if (retry)
            {
                database.SchemaProvisioner.JointStage = stage => { if (stage == "OwnersDurable") { throw new InvalidOperationException("first attempt fails"); } };
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(target, guard.Token));
                failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
                database.SchemaProvisioner.JointStage = null;
            }
            else
            {
                await using var session = await database.CreateSessionAsync(guard.Token);
                await session.ExecuteAsync("CREATE TABLE scratch(v INT)", cancellationToken: guard.Token);
                await session.ExecuteAsync("INSERT INTO scratch VALUES(1)", cancellationToken: guard.Token);
                await session.ExecuteAsync("DROP TABLE scratch", cancellationToken: guard.Token);
            }
            await entered.Task.WaitAsync(guard.Token);
            using var recorder = new Assimalign.Cohesion.Testing.EventSourceRecorder(EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
            database.Coordinator.TryCheckpoint(TimeSpan.Zero, guard.Token).ShouldBeFalse();
            recorder.Events.ShouldContain(e => e.EventId == 21 && Equals(e.Payload![1], "SqlTableReclamation"));
            release.Set();
            SqlPlanExecutor.RewriteStage = (stage, _) =>
            {
                if (stage == "CheckpointBusy") { Interlocked.Increment(ref busy); }
                if (stage == "CopyCheckpointed") { Interlocked.Increment(ref checkpoints); if (cancel) { cancellation.Cancel(); } }
            };

            // Act
            if (cancel) { await Should.ThrowAsync<OperationCanceledException>(async () => await database.ApplySchemaAsync(target, cancellation.Token)); }
            else { await database.ApplySchemaAsync(target, guard.Token); }

            // Assert: sample before truncation, including the held reclamation backlog.
            batches.ShouldBeGreaterThan(0);
            busy.ShouldBe(0);
            checkpoints.ShouldBeGreaterThan(0);
            peak.ShouldBeLessThanOrEqualTo(SqlSchemaProvisioner.JointCopyCheckpointBytes + 128L * 2 * (Page.Size + 46) + 152);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(cancel ? source.Hash : target.Hash);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.IsOffline.ShouldBeFalse();
            _output.WriteLine($"reclamation retry={retry}: batches={batches}; busy={busy}; checkpoints={checkpoints}; pre-truncation peak={peak}");
        }
        finally
        {
            release.Set();
            SqlPlanExecutor.RewriteStage = null;
            storage.DurableStorageChanged = null;
            database.TableReclamation.BeforeBatchCommit = null;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gate")]
    private static extern ref SemaphoreSlim AdmissionSemaphore(TransactionCheckpointInterlock interlock);

    [Fact(DisplayName = "Cohesion Test [Sql] - Reclamation durability waits outside statement admission (#1283)")]
    public async Task RunBatch_DurabilityFlush_ShouldLeaveStatementAdmissionFree()
    {
        // Arrange: inspect the real semaphore during the journal's synchronous fsync.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE retired(v INT)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO retired VALUES(1)", cancellationToken: guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            int batchThread = 0, observations = 0, admissions = 0;
            database.TableReclamation.BeforeBatchCommit = (_, _) => batchThread = Environment.CurrentManagedThreadId;
            database.TableReclamation.AfterBatch = (_, _) => batchThread = 0;
            storage.DurableStorageChanged = catalog =>
            {
                if (catalog || batchThread != Environment.CurrentManagedThreadId) { return; }
                Interlocked.Increment(ref observations);
                if (AdmissionSemaphore(TransactionCheckpointInterlock.ForStorage(database.DataStorage)).CurrentCount == 1)
                { Interlocked.Increment(ref admissions); }
            };
            try
            {
                // Act
                await session.ExecuteAsync("DROP TABLE retired", cancellationToken: guard.Token);
                database.TableReclamation.RunBatch(guard.Token);
                // Assert: every data durability observation between commit and AfterBatch
                // sees a free admission permit; no duration decides the result.
                observations.ShouldBeGreaterThan(0);
                admissions.ShouldBe(observations);
            }
            finally
            {
                storage.DurableStorageChanged = null;
                database.TableReclamation.BeforeBatchCommit = null;
                database.TableReclamation.AfterBatch = null;
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint checkpoint: untouched writers' existing WAL does not trigger a copy checkpoint")]
    public async Task ApplyAsync_UnrelatedWal_ShouldCountOnlyCopyBrackets()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(NarrowDeclaration(false, false), guard.Token);
        SeedRaw(database, [1, 1], 1);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE untouched(payload VARCHAR(4000))", cancellationToken: guard.Token);
        database.Catalog.TryGetTable("dbo", "untouched", out var table).ShouldBeTrue();
        byte[] record = SqlRowCodec.Encode(table, [new string('x', 4000)], TransactionSequence.None);
        for (int first = 0; first < 4096; first += 64)
        {
            using var bracket = database.DataStorage.BeginTransaction();
            for (int row = 0; row < 64; row++) { database.DataStorage.InsertRow(bracket, table.ObjectId, record); }
            bracket.Commit();
        }
        long before = database.DataStorage.Journal.Length;
        before.ShouldBeGreaterThan(SqlSchemaProvisioner.JointCopyCheckpointBytes);

        // Act
        await database.ApplySchemaAsync(NarrowDeclaration(true, false), guard.Token);

        // Assert
        database.DataStorage.Journal.Length.ShouldBeGreaterThanOrEqualTo(before);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(NarrowDeclaration(true, false).Hash);
        _output.WriteLine($"unrelated WAL={before}; after small copy={database.DataStorage.Journal.Length}");
    }
}
