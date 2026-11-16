using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTextIndexAdmissionIntegrationTests
{
    private readonly ITestOutputHelper _output;
    public GraphTextIndexAdmissionIntegrationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Text CREATE and DROP wait outside metadata monitors during a held checkpoint (#1283, #1644)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PausedTextDdl_ShouldServeMetadataAndResume(bool drop)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = GraphTextIndexTests.Quiet(); options.CheckpointJournalSize = 0;
        await using var engine = GraphDatabaseEngine.Create("text-ddl-pause", options);
        var database = await engine.CreateDatabaseAsync("graph", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT (:N {k:1,p:'abcd'})", cancellationToken: guard.Token);
        if (drop) { await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: guard.Token); }
        database.Coordinator.Checkpoint(guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        database.DataStorage.CheckpointJournalSize = 1; gate.JournalSizeLimit = 2;
        using var holder = database.DataStorage.BeginTransaction("HeldTextDdlCheckpoint");
        gate.ObserveAdmissionPause().ShouldBeTrue();
        using var recorder = RecordAdmission();
        var writing = Task.Run(async () => await session.ExecuteAsync(drop ? "DROP INDEX t" : "CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: guard.Token), guard.Token);
        try
        {
            await WaitForAdmissionAsync(recorder, guard.Token);
            // Act: metadata and store directory readers need the monitors DDL must release.
            var schema = GraphSchema.Open(database, reader);
            (await schema.GetLabelsAsync(guard.Token)).ShouldHaveSingleItem();
            (await schema.GetIndexesAsync(guard.Token)).Count.ShouldBe(drop ? 1 : 0);
            (await GraphTextIndexTests.RowsAsync(reader, "MATCH (n:N) RETURN n.k")).ShouldHaveSingleItem()[0].ShouldBe(1L);
            writing.IsCompleted.ShouldBeFalse();
            gate.JournalSizeLimit = 0; holder.Rollback(); database.Coordinator.Checkpoint(guard.Token);
            await writing.WaitAsync(guard.Token);
            // Assert
            (await schema.GetIndexesAsync(guard.Token)).Count.ShouldBe(drop ? 0 : 1);
            if (!drop)
            {
                var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
                ulong id = database.Store.GetIndexIdentities(context.Snapshot).Single().Id;
                await database.Coordinator.RollbackAsync(context, guard.Token);
                (await GraphTextIndexTests.CountEntriesAsync(database, id)).ShouldBe(2);
            }
            database.IsOffline.ShouldBeFalse();
        }
        finally { gate.JournalSizeLimit = 0; holder.Dispose(); await writing.WaitAsync(guard.Token); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Text maintenance resumes once and reclamation yields during a held checkpoint (#1283, #1644)")]
    public async Task ExecuteAsync_PausedTextMaintenance_ShouldKeepEveryPostingAndYieldReclamation()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = GraphTextIndexTests.Quiet(); options.CheckpointJournalSize = 0;
        await using var engine = GraphDatabaseEngine.Create("text-maintenance-pause", options);
        var database = await engine.CreateDatabaseAsync("graph", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT (:N {k:1,p:'abcd'})", cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: guard.Token);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        ulong id = database.Store.GetIndexIdentities(context.Snapshot).Single().Id;
        await database.Coordinator.RollbackAsync(context, guard.Token);
        database.Coordinator.Checkpoint(guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        database.DataStorage.CheckpointJournalSize = 1; gate.JournalSizeLimit = 2;
        using var holder = database.DataStorage.BeginTransaction("HeldTextMaintenanceCheckpoint");
        gate.ObserveAdmissionPause().ShouldBeTrue();
        using var recorder = RecordAdmission();
        var writing = Task.Run(async () => await session.ExecuteAsync("MATCH (n:N) SET n.p='abcxyz'", cancellationToken: guard.Token), guard.Token);
        try
        {
            await WaitForAdmissionAsync(recorder, guard.Token);
            // Act
            await Task.Run(() => database.Coordinator.RunVersionPurgePass(guard.Token), guard.Token).WaitAsync(guard.Token);
            gate.IsAdmissionPaused.ShouldBeTrue(); writing.IsCompleted.ShouldBeFalse();
            gate.JournalSizeLimit = 0; holder.Rollback(); database.Coordinator.Checkpoint(guard.Token);
            await writing.WaitAsync(guard.Token);
            // Assert: four distinct new trigrams, one visible posting each; old bcd is gone.
            var postings = await GraphTextIndexTests.CountEntriesByTokenAsync(database, id);
            postings.Count.ShouldBe(4); postings.Values.ShouldAllBe(count => count == 1);
            (await GraphTextIndexTests.RowsAsync(session, GraphTextIndexTests.Query("CONTAINS"), "bcd")).ShouldBeEmpty();
            (await GraphTextIndexTests.RowsAsync(session, GraphTextIndexTests.Query("CONTAINS"), "xyz")).ShouldHaveSingleItem();
            await session.ExecuteAsync("DROP INDEX t", cancellationToken: guard.Token);
            database.Coordinator.Checkpoint(guard.Token);
            using var retiredHolder = database.DataStorage.BeginTransaction("HeldTextReclamationCheckpoint");
            gate.JournalSizeLimit = 2; gate.ObserveAdmissionPause().ShouldBeTrue();
            int pages = database.DataStorage.GetOwnerIndexPages(id).Count;
            pages.ShouldBeGreaterThan(0);
            await Task.Run(() => database.Coordinator.RunVersionPurgePass(guard.Token), guard.Token).WaitAsync(guard.Token);
            database.DataStorage.GetOwnerIndexPages(id).Count.ShouldBe(pages);
            gate.JournalSizeLimit = 0; retiredHolder.Rollback();
            database.DataStorage.CheckpointJournalSize = 0;
            database.Coordinator.Checkpoint(guard.Token);
            database.Coordinator.RunVersionPurgePass(guard.Token);
            database.DataStorage.GetOwnerIndexPages(id).ShouldBeEmpty();
        }
        finally { gate.JournalSizeLimit = 0; holder.Dispose(); await writing.WaitAsync(guard.Token); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - A text backfill crossing J drains only its admitted bracket and lifecycle commit (#1283, #1644)")]
    public async Task ExecuteAsync_TextBuildCrossesJournalLimit_ShouldDrainOnlyAdmittedBracket()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var options = GraphTextIndexTests.Quiet(); options.CheckpointJournalSize = 0;
        await using var engine = GraphDatabaseEngine.Create("text-build-bracket", options);
        var database = await engine.CreateDatabaseAsync("graph", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("FOR k IN [1,2,3,4,5,6,7,8] INSERT (:N {k:k,p:$pattern})", GraphTextIndexTests.Bind(GraphTextIndexTests.UniqueAscii(1024)), guard.Token);
        database.Coordinator.Checkpoint(guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        using var holder = database.DataStorage.BeginTransaction("HeldTextBuildCheckpoint");
        int inserted = 0, beginsAtPause = 0;
        database.Store.AfterTextIndexMutation = stage =>
        {
            if (stage != "build-entry") { return; }
            if (++inserted != 1) { return; }
            database.DataStorage.CheckpointJournalSize = 1; gate.JournalSizeLimit = 2;
            gate.ObserveAdmissionPause().ShouldBeTrue();
            beginsAtPause = database.DataStorage.WriteAheadJournal.ReadAll().Count(r => r.Type == JournalRecordType.BeginTransaction);
        };
        using var admissionEvents = RecordAdmission();
        var building = Task.Run(async () => await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: guard.Token), guard.Token);
        try
        {
            // Act: the first admitted chunk drains, then the next chunk waits outside
            // the apply gate until a checkpoint can end the pause.
            await WaitForAdmissionAsync(admissionEvents, guard.Token);
            building.IsCompleted.ShouldBeFalse();
            // Assert: only the admitted build bracket grew past J, by its measured bytes.
            inserted.ShouldBe(64); gate.IsAdmissionPaused.ShouldBeTrue();
            database.DataStorage.WriteAheadJournal.ReadAll().Count(r => r.Type == JournalRecordType.BeginTransaction).ShouldBe(beginsAtPause);
            long peak = database.DataStorage.JournalLength;
            peak.ShouldBeGreaterThan(2);
            _output.WriteLine($"TEXT_BUILD_BRACKET|entries={inserted}|J=2|journal={peak}|additionalBrackets=0");
            gate.JournalSizeLimit = 0; holder.Rollback(); database.Coordinator.Checkpoint(guard.Token);
            await building.WaitAsync(guard.Token);
            inserted.ShouldBe(8 * 1022);
            (await GraphSchema.Open(database, session).GetIndexesAsync(guard.Token)).ShouldHaveSingleItem().IsText.ShouldBeTrue();
        }
        finally { database.Store.AfterTextIndexMutation = null; gate.JournalSizeLimit = 0; holder.Dispose(); await building.WaitAsync(guard.Token); }
    }

    private static EventSourceRecorder RecordAdmission() => new(EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
    private static async Task WaitForAdmissionAsync(EventSourceRecorder recorder, CancellationToken token)
    { while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, token); } }
}
