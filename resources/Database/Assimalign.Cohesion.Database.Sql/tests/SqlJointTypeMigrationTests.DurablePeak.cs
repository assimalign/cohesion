using System;
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
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Wide keyed rewrite bounds include its durable crossing bracket (#1778)")]
    public async Task ApplyAsync_WideKeyedRewrite_ShouldBoundDurablePeak()
    {
        // Arrange: random keys and 4 KiB rows make threshold-crossing brackets large.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        const int rows = 65536;
        var storage = new CrashCaptureSqlStorageStrategy(); var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        SqlCompiledSchema Declare(bool target) => Compiled(new CompiledSchemaTable("items", "row",
            [new("Id", DatabaseType.Int32, false), new("Value", target ? DatabaseType.Int64 : DatabaseType.Int32, false), new("Payload", DatabaseType.String, false, MaxLength: 4000)],
            new("pk", ["Id"]), [], []));
        await using var engine = await builder.BuildAsync(guard.Token); var db = await engine.CreateDatabaseAsync("typed", guard.Token);
        await db.ApplySchemaAsync(Declare(false), guard.Token);
        {
            var keys = Enumerable.Range(0, rows).ToArray(); new Random(177804).Shuffle(keys);
            await using var s = await db.CreateSessionAsync(guard.Token);
            for (int first = 0; first < rows; first += 64)
            {
                await s.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', keys.Skip(first).Take(64).Select(k => $"({k},{k},'{new string('x', 4000)}')")), cancellationToken: guard.Token);
                if ((first + 64) % 4096 == 0) { db.Coordinator.Checkpoint(guard.Token); }
            }
        }
        db.Coordinator.Checkpoint(guard.Token);
        int height = SqlSchemaProvisioner.JointTreeHeight(db.DataStorage, db.IndexManager.ExportRegistrations().Single().RootPageId, db.IndexManager.PageOwner(db.IndexManager.ExportRegistrations().Single()), out _);
        long reserve = SqlSchemaProvisioner.JointCopyCheckpointBytes + 64L * (2 + 4 * height + 3) * 2 * (Page.Size + 46) + 152;
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(e => e.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Informational);
        using var reclamations = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        var sampleGate = new object();
        string jointStage = "JobStart", peakStage = "JobStart";
        bool publicationCommitted = false, peakPublished = false, sampling = true;
        long publicationLength = 0, beforePeak = 0, afterPeak = 0, reclaimedAtPeak = 0;
        DateTime peakTime = default;
        long peak = 0, largest = 0, bracketStart = 0;
        bool copyingBracket = false, peakDuringBracket = false;
        int forced = 0, observed = 0, busy = 0;
        storage.DurableStorageChanged = catalog =>
        {
            if (catalog) { return; }
            long length = db.DataStorage.JournalLength;
            lock (sampleGate)
            {
                if (!sampling) { return; }
                if (publicationCommitted) { afterPeak = Math.Max(afterPeak, length); }
                else
                {
                    beforePeak = Math.Max(beforePeak, length);
                    if (copyingBracket) { largest = Math.Max(largest, length - bracketStart); }
                }
                if (length > peak)
                {
                    peak = length; peakDuringBracket = copyingBracket; peakStage = jointStage;
                    peakPublished = publicationCommitted; reclaimedAtPeak = db.TableReclamation.ReclaimedPages;
                    peakTime = DateTime.UtcNow;
                }
            }
        };
        db.SchemaProvisioner.JointStage = stage =>
        {
            long length = db.DataStorage.JournalLength;
            lock (sampleGate)
            {
                jointStage = stage;
                if (stage == "CatalogPublished")
                {
                    publicationLength = length;
                    beforePeak = Math.Max(beforePeak, length);
                    publicationCommitted = true;
                }
            }
        };
        SqlPlanExecutor.RewriteStage = (stage, length) =>
        {
            long start = stage == "CopyAllocationsCaptured" ? db.DataStorage.JournalLength - 38 : 0;
            lock (sampleGate)
            {
                if (stage == "CopyAllocationsCaptured") { bracketStart = start; copyingBracket = true; }
                if (stage == "CopyCommitted") { copyingBracket = false; }
                if (stage == "CopyCheckpointed") { forced++; }
                if (stage == "CopyCheckpointObserved") { observed++; }
                if (stage == "CheckpointBusy") { busy++; }
            }
        };
        try
        {
            // Act: publication closes the asserted job window; keep later work diagnostic only.
            await db.SchemaProvisioner.ApplyAsync(Declare(true), guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(db, guard.Token);
            int completed = events.Events.Count(e => e.EventId == 6 && Equals(e.Payload![0], db.DataStorage.Name.ToString()) && Equals(e.Payload[1], "Success"));
            await using var reader = await db.CreateSessionAsync(guard.Token);
            long published = Convert.ToInt64((await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0]);
            lock (sampleGate) { sampling = false; }

            // Assert: both bounds include durable crossing brackets through CatalogPublished.
            _output.WriteLine($"wide keyed seed=177804 rows={rows} T={SqlSchemaProvisioner.JointCopyCheckpointBytes} beforePeak={beforePeak} publicationLength={publicationLength} afterPeak={afterPeak} largest={largest} height={height} bound={SqlSchemaProvisioner.JointCopyCheckpointBytes + largest} J={reserve} completed={completed} forced={forced} observed={observed} busy={busy} published={published} failure={db.Provisioning?.FailureCode}");
            _output.WriteLine($"overall peak={peak}; peakStage={peakStage}; publicationCommitted={peakPublished}; peakDuringBracket={peakDuringBracket}; reclaimedAtPeak={reclaimedAtPeak}; reclamationsAtPeak={string.Join(',', reclamations.Events.Where(e => e.TimeStamp.ToUniversalTime() <= peakTime && e.EventName!.StartsWith("TableReclamation", StringComparison.Ordinal)).Select(e => e.EventName))}");
            publicationCommitted.ShouldBeTrue();
            db.Provisioning.ShouldBeNull(); published.ShouldBe(rows); busy.ShouldBe(0);
            beforePeak.ShouldBeGreaterThan(SqlSchemaProvisioner.JointCopyCheckpointBytes);
            largest.ShouldBeGreaterThan(0);
            beforePeak.ShouldBeLessThanOrEqualTo(SqlSchemaProvisioner.JointCopyCheckpointBytes + largest);
            beforePeak.ShouldBeLessThanOrEqualTo(reserve);
            forced.ShouldBeGreaterThan(0); observed.ShouldBe(0);
            db.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(true).Hash); db.IsOffline.ShouldBeFalse();
        }
        finally
        {
            lock (sampleGate) { sampling = false; }
            storage.DurableStorageChanged = null;
            SqlPlanExecutor.RewriteStage = null;
            db.SchemaProvisioner.JointStage = null;
        }
    }
}
