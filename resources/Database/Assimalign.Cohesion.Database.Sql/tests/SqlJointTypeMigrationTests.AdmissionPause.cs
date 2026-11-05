using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Reclamation yields to paused journal admission and resumes after its checkpoint (#1283)")]
    public async Task RunBatch_JournalPaused_ShouldYieldThenReclaim()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("reclamation-pause", new()
        { CheckpointJournalSize = 0, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("paused", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE retired(id INT)", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO retired VALUES(1)", cancellationToken: guard.Token);
            await session.ExecuteAsync("DROP TABLE retired", cancellationToken: guard.Token);
            var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
            gate.JournalSizeLimit = 2; database.DataStorage.CheckpointJournalSize = 1;
            using var outside = database.DataStorage.BeginTransaction("OutsideTest");
            gate.IsAdmissionPaused.ShouldBeTrue();
            // The pump is held: discard its shared wake so this yield proves the storage due predicate.
            engine.CheckpointNeededSignal.Reset();
            int visits = 0;
            database.TableReclamation.BeforeBatch = (_, _) => Interlocked.Increment(ref visits);
            long before = database.TableReclamation.ReclaimedPages;
            try
            {
                // Act / Assert: the pending predicate yields before reclamation enters admission.
                await Task.Factory.StartNew(() => database.TableReclamation.RunBatch(guard.Token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(guard.Token);
                visits.ShouldBeGreaterThan(0);
                database.TableReclamation.ReclaimedPages.ShouldBe(before);
                outside.Rollback(); database.Coordinator.Checkpoint(guard.Token);
                engine.CheckpointNeededSignal.Reset();
                database.DataStorage.CheckpointJournalSize = 0;
                database.TableReclamation.RunBatch(guard.Token);
                database.TableReclamation.ReclaimedPages.ShouldBeGreaterThan(before);
            }
            finally { database.TableReclamation.BeforeBatch = null; }
        });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint migration worker and inline migration drain admission pauses (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_AdmissionPauseDuringJointCopy_ShouldPublishWithoutSelfWait(bool deferred)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var engine = await Builder(await SeedAsync(guard.Token)).BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        gate.JournalSizeLimit = 2; database.DataStorage.CheckpointJournalSize = 1;
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        // Act
        if (deferred)
        {
            await database.SchemaProvisioner.ApplyAsync(Target().Compile(), guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);
        }
        else { await database.ApplySchemaAsync(Target().Compile(), guard.Token); }
        // Assert
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
        database.IsOffline.ShouldBeFalse();
        // A reclamation bracket may already start another due cycle after publication.
        // Prove the migration completed and a checkpoint resumed its earlier pause.
        recorder.Events.ShouldContain(e => e.EventId == 24 && Equals(e.Payload![0], database.DataStorage.Name.ToString())
            && Equals(e.Payload[1], "CheckpointCompleted"));
        recorder.Events.ShouldContain(e => e.EventId == 23 && Equals(e.Payload![0], database.DataStorage.Name.ToString()));
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint copy forced checkpoints bypass their own admission pause (#1283)")]
    public async Task ExecuteAsync_ForcedCopyCheckpointWhilePaused_ShouldDrainAndAdmitItsNextBracket()
    {
        // Arrange: a small internal copy threshold exercises the real copy/checkpoint loop.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var builder = Builder(new());
        builder.Options.CheckpointJournalSize = 0; builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Compiled(new CompiledSchemaTable("items", "row",
            [new("Value", DatabaseType.Int32, false)], null, [], [])), guard.Token);
        SeedRaw(database, [1], 128); database.Coordinator.Checkpoint(guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        gate.JournalSizeLimit = 2;
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        var statement = new SqlStatementContext(transaction, database.Coordinator, provisioningSchema: "typed",
            provisioningProgress: (_, _) => { }, checkpointCopy: true);
        statement.ConfigureCopySpace((_, _, _, _) => { }, _ => { }, () => 0, checkpointBytes: 1);
        var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
        int paused = 0, forced = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "CopyCommitted")
            {
                database.DataStorage.CheckpointJournalSize = 1;
                if (gate.IsAdmissionPaused) { paused++; }
            }
            if (stage == "CopyCheckpointed") { forced++; database.DataStorage.CheckpointJournalSize = 0; }
        };
        try
        {
            // Act
            var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("CREATE INDEX ix ON items(Value)").Statement.SqlExpression);
            await executor.ExecuteAsync(plan, statement, guard.Token);
            // Assert: forced checkpoint can run while paused and subsequent brackets make progress.
            paused.ShouldBeGreaterThan(0); forced.ShouldBeGreaterThan(0);
            gate.IsAdmissionPaused.ShouldBeFalse(); database.IsOffline.ShouldBeFalse();
        }
        finally
        {
            SqlPlanExecutor.RewriteStage = null;
            database.DataStorage.CheckpointJournalSize = 0;
            database.Coordinator.Checkpoint(guard.Token);
            await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
        }
    }
}
