using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection(nameof(KeyValueWorkerResilienceTests))]
public sealed class CheckpointBracketLoadTests
{
    private readonly ITestOutputHelper _output;
    public CheckpointBracketLoadTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Both catalog write classes attribute and service their deferred checkpoint (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryCheckpoint_CatalogWriteHeld_ShouldAttributeAndCollectBeforeRelease(bool format)
    {
        // Arrange: prime the data coordinator, then park a real catalog append on its own gate.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = KeyValueDatabaseEngine.Create("catalog-brackets", new()
        {
            CheckpointJournalSize = 0,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("catalog-" + Guid.NewGuid().ToString("N"), ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        await database.PutAsync(session, new byte[] { 1 }, new byte[] { 2 }, cancellationToken: ceiling.Token);
        var registrations = database.Catalog.GetIndexRegistrations().ToArray();
        int version = database.Catalog.EntrySpaceFormatVersion;
        var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var gate = TransactionCheckpointInterlock.ForStorage(database.CatalogStorage);
        gate.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeTrue();
        Action? original = database.CatalogStorage.OnCheckpointNeeded;
        database.CatalogStorage.OnCheckpointNeeded = () => { entered.Set(); release.Wait(ceiling.Token); };
        database.CatalogStorage.CheckpointJournalSize = 1;
        var writing = Task.Factory.StartNew(() =>
        {
            if (format) { database.Catalog.SetEntrySpaceFormatVersionAsync(version, ceiling.Token).GetAwaiter().GetResult(); }
            else { database.Catalog.SaveIndexRegistrationsAsync(registrations, ceiling.Token).GetAwaiter().GetResult(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            entered.Wait(ceiling.Token);
            // Act: a catalog scope cannot block the independent data gate.
            database.Coordinator.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeTrue();
            gate.TryCheckpoint(TimeSpan.Zero, ceiling.Token).ShouldBeFalse();
            release.Set();
            await writing.WaitAsync(ceiling.Token);
            // Assert: scope disposal collected the request, leaving no catalog checkpoint due.
            database.CatalogStorage.IsCheckpointDue(TimeSpan.FromHours(1)).ShouldBeFalse();
            var blocked = recorder.Events.Where(e => e.EventId == 21 && Equals(e.Payload![0], (string)database.CatalogStorage.Name)).ToArray();
            blocked.ShouldNotBeEmpty();
            blocked.ShouldAllBe(e => Equals(e.Payload![1], "KeyValuePairCatalog") && Equals(e.Payload![2], 0L));
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            release.Set();
            try { await writing.WaitAsync(ceiling.Token); }
            finally
            {
                database.CatalogStorage.CheckpointJournalSize = 0;
                database.CatalogStorage.OnCheckpointNeeded = original;
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Checkpoint: four continuous writers on three Windows cores (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_FourContinuousWriters_ShouldRecordBoundedProgress(bool admissionPause)
    {
        // Arrange
        await using var engine = KeyValueDatabaseEngine.Create("bracket-load", new()
        {
            CheckpointJournalSize = CheckpointLoad.JournalSize,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("load-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        var sessions = new KeyValueDatabaseSession[4];
        try
        {
            for (int i = 0; i < sessions.Length; i++) { sessions[i] = await database.CreateSessionAsync(); }
            // Act / Assert: the shared harness records progress, bytes and checkpoint outcomes.
            await CheckpointLoad.RunAsync(database.DataStorage, database.Coordinator.Checkpoint, async (writer, next, token) =>
            {
                var session = sessions[writer];
                await database.PutAsync(session, System.Text.Encoding.UTF8.GetBytes($"{writer}-{next}"), new byte[32], cancellationToken: token);
            }, _output.WriteLine, admissionPause);
        }
        finally
        {
            foreach (var session in sessions) { if (session is not null) { await session.DisposeAsync(); } }
        }
    }
}
