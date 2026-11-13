using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexBuildJournalTests
{
    private readonly ITestOutputHelper _output;
    public DocumentIndexBuildJournalTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - Every checkpoint peak fits the trigger plus one measured chunk (#1808)")]
    public async Task CreateIndexAsync_ChunkCommits_ShouldBoundEveryCheckpointPeak()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        const long trigger = 64 * 1024;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = DocumentDatabaseEngine.Create("journal-chunks", new()
        { StorageStrategy = strategy, CheckpointJournalSize = trigger, CheckpointInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        var collection = await session.CreateCollectionAsync("items", guard.Token);
        for (int row = 0; row < 4096; row++)
        { await collection.PutAsync(session, row.ToString("D4"), "{\"v\":1}"u8.ToArray(), cancellationToken: guard.Token); }
        database.Coordinator.Checkpoint(guard.Token);
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Informational);
        bool inChunk = false;
        long start = 0, committedLength = 0;
        int chunks = 0;
        var commits = new List<long>(); var deltas = new List<long>();
        database.Catalog.AfterIndexRetirementMutation = stage =>
        {
            if (stage == "build-chunk-start") { start = database.DataStorage.JournalLength; committedLength = start; inChunk = true; }
            if (stage == "build-chunk") { chunks++; inChunk = false; commits.Add(committedLength); deltas.Add(committedLength - start); }
        };
        FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () =>
        {
            if (!inChunk || !database.DataStorage.HasActiveTransactions) { return; }
            long length = database.DataStorage.JournalLength;
            committedLength = Math.Max(committedLength, length);
        };

        // Act.
        try { await session.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token); }
        finally { database.Catalog.AfterIndexRetirementMutation = null; FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }
        database.Coordinator.Checkpoint(guard.Token);

        // Assert: includes final publication metadata and every checkpoint start.
        commits.Count.ShouldBe(chunks); chunks.ShouldBe(64);
        var peaks = events.Events.Where(item => item.EventId == 5 && Equals(item.Payload![0], database.DataStorage.Name.ToString()))
            .Select(item => (long)item.Payload![2]!).ToArray();
        peaks.ShouldNotBeEmpty();
        peaks.Length.ShouldBeGreaterThan(1, "size-triggered checkpoints must occur during the build");
        foreach (long peak in peaks) { peak.ShouldBeLessThanOrEqualTo(trigger + deltas.Max()); }
        _output.WriteLine($"Documents chunks={chunks}; largest delta={deltas.Max()}; checkpoint peak={peaks.Max()}; commits={string.Join(',', commits)}");
    }
}
