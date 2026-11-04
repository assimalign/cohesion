using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Copy checkpoint fix: hundreds of buffered Begin frames count exact journal growth (#1778)")]
    public async Task ExecuteAsync_CheckpointFixBracketDeltas_ShouldEqualJournalGrowth()
    {
        // Arrange: exercise the real deferred index builder with an inspectable context.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var builder = Builder(new());
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Compiled(new CompiledSchemaTable("items", "row",
            [new("Value", DatabaseType.Int32, false)], null, [], [])), guard.Token);
        SeedRaw(database, [1], 32768);
        database.Coordinator.Checkpoint(guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
            // Enroll the logical writer before measuring physical copy brackets.
            // Its lazy Begin is emitted by EnterWriter outside the first bracket.
            await database.Coordinator.ApplyStatementAsync(transaction, _ => new ValueTask<bool>(true), durable: true, cancellationToken: guard.Token);
            var statement = new SqlStatementContext(transaction, database.Coordinator, provisioningSchema: "typed",
                provisioningProgress: (_, _) => { }, checkpointCopy: true);
            statement.ConfigureCopySpace((_, _, _, _) => { }, _ => { }, () => 0);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            long start = database.DataStorage.JournalLength;
            var samples = new List<(long Counter, long Growth)>();
            int observed = 0;
            SqlPlanExecutor.RewriteStage = (stage, length) =>
            {
                if (stage == "CopyCommitted") { samples.Add((statement.AddCopyJournalBytes(0), length - start)); }
                if (stage == "CopyCheckpointObserved") { observed++; }
            };
            try
            {
                // Act
                var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("CREATE INDEX ix ON items(Value)").Statement.SqlExpression);
                await executor.ExecuteAsync(plan, statement, guard.Token);

                // Assert: root plus 512 row brackets; every delta includes Begin exactly once.
                samples.Count.ShouldBe(513);
                _output.WriteLine($"Exact deltas: brackets={samples.Count}; counter={samples[^1].Counter}; growth={samples[^1].Growth}; observed={observed}");
                observed.ShouldBe(0);
                samples.Count(sample => sample.Counter != sample.Growth).ShouldBe(0);
            }
            finally
            {
                SqlPlanExecutor.RewriteStage = null;
                await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
            }
        });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Copy checkpoint fix: narrow rewrites obey their journal reserve and observe only real checkpoints (#1778)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ApplyAsync_CheckpointFixNarrowRewrite_ShouldBoundPeak(bool keyed, bool engineCheckpoint)
    {
        // Arrange: narrow brackets expose the accumulated 38-byte error of wide fixtures.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = engineCheckpoint ? 1024 * 1024 : 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        SqlCompiledSchema Declare(bool changed) => Compiled(new CompiledSchemaTable("items", "row",
            [new("Id", changed ? DatabaseType.Int64 : DatabaseType.Int32, false)], keyed ? new("pk", ["Id"]) : null, [], []));
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        const int rows = 524288;
        if (keyed)
        {
            await using var writer = await database.CreateSessionAsync(guard.Token);
            for (int first = 0; first < rows; first += 64)
            {
                await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(first, 64).Select(id => $"({id})")), cancellationToken: guard.Token);
                if ((first + 64) % 4096 == 0) { database.Coordinator.Checkpoint(guard.Token); }
            }
        }
        else { SeedRaw(database, [1], rows); }
        database.Coordinator.Checkpoint(guard.Token);
        int height = keyed ? SqlSchemaProvisioner.JointTreeHeight(database.DataStorage,
            database.IndexManager.ExportRegistrations().Single().RootPageId, Table(database).ObjectId, out _) : 0;
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage");
        using var events = new EventSourceRecorder(source, EventLevel.Informational);
        int observed = 0, forced = 0, completedAtObservation = 0;
        bool observationWithoutCheckpoint = false;
        long peak = 0;
        storage.DurableStorageChanged = catalog =>
        { if (!catalog) { peak = Math.Max(peak, database.DataStorage.JournalLength); } };
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "CopyCheckpointed") { forced++; }
            if (stage != "CopyCheckpointObserved") { return; }
            observed++;
            int completed = events.Events.Count(entry => entry.EventId == 6 && Equals(entry.Payload![0], database.DataStorage.Name.ToString()) && Equals(entry.Payload[1], "Success"));
            observationWithoutCheckpoint |= completed <= completedAtObservation;
            completedAtObservation = completed;
        };
        try
        {
            // Act
            await database.ApplySchemaAsync(Declare(true), guard.Token);

            // Assert: retained key trees contribute their measured source height to J.
            long reserve = SqlSchemaProvisioner.JointCopyCheckpointBytes + 64L * (2 + (keyed ? 4 * height + 3 : 0)) * 2 * (Page.Size + 46) + 152;
            reserve.ShouldBe(keyed ? 34_703_256 : 18_886_296);
            _output.WriteLine($"Narrow rewrite: keyed={keyed}; height={height}; engine={engineCheckpoint}; peak={peak}; J={reserve}; observed={observed}; forced={forced}; engine checkpoints={events.Events.Count(entry => entry.EventId == 6 && Equals(entry.Payload![0], database.DataStorage.Name.ToString()) && Equals(entry.Payload[1], "Success"))}");
            if (!keyed) { peak.ShouldBeLessThanOrEqualTo(18_886_424); }
            observationWithoutCheckpoint.ShouldBeFalse();
            peak.ShouldBeLessThanOrEqualTo(reserve);
            if (engineCheckpoint) { observed.ShouldBeGreaterThan(0); }
            else { observed.ShouldBe(0); forced.ShouldBeGreaterThan(0); }
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(true).Hash);
            await using var reader = await database.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe((long)rows);
            database.IsOffline.ShouldBeFalse();
        }
        finally { SqlPlanExecutor.RewriteStage = null; storage.DurableStorageChanged = null; }
    }
}
