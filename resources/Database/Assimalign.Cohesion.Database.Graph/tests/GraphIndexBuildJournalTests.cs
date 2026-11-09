using System;
using System.Buffers.Binary;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphIndexBuildJournalTests
{
    private readonly ITestOutputHelper _output;
    public GraphIndexBuildJournalTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Text backfill journal peak is bounded by a chunk at a due checkpoint (#1808)")]
    [InlineData(64 * 1024)]
    [InlineData(256 * 1024)]
    public async Task CreateIndexAsync_TextBackfill_ShouldReduceWholeBracketJournalPeak(long trigger)
    {
        // Arrange: compare the former one-bracket insertion shape with the shared loop.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var options = GraphTextIndexTests.Quiet();
        options.StorageStrategy = new FaultInjectingJournalStorageStrategy(durable: true);
        options.CheckpointJournalSize = trigger;
        await using var engine = GraphDatabaseEngine.Create("text-chunks", options);
        var database = await engine.CreateDatabaseAsync("graph", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        string value = GraphTextIndexTests.UniqueAscii(1024);
        await session.ExecuteAsync("FOR k IN [1,2,3,4,5,6,7,8] INSERT (:N {k:k,p:$pattern})", GraphTextIndexTests.Bind(value), guard.Token);
        database.Coordinator.Checkpoint(guard.Token);
        var source = EventSource.GetSources().Single(item => item.Name == "Assimalign.Cohesion.Database.Storage");
        using var events = new EventSourceRecorder(source, EventLevel.Informational);
        long[] peaks = new long[2];
        long Peak(int skip) => events.Events.Skip(skip)
            .Where(item => item.EventId == 5 && Equals(item.Payload![0], database.DataStorage.Name.ToString()))
            .Select(item => (long)item.Payload![2]!).DefaultIfEmpty().Max();
        var nodes = new List<(ulong Id, ulong Location, TransactionSequence Writer)>();
        using (var records = database.DataStorage.GetUnitIterator(2))
        {
            while (records.MoveNext())
            {
                var unit = records.Current;
                var record = GraphRecordCodec.Decode(unit.Data, unit.PageId, unit.SlotIndex);
                if (record.Node is not { } node) { continue; }
                var (writer, _) = RecordVersionStamp.ReadStamps(unit.Data.Span);
                nodes.Add((node.Id, GraphStorage.PackLocation(unit.PageId, unit.SlotIndex), writer));
            }
        }
        nodes.Count.ShouldBe(8);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        ulong owner = (ulong)database.DataStorage.ReserveTransactionSequence();
        var physical = BTreeIndexManager.Create(new()
        {
            Storage = database.DataStorage,
            TransactionSource = transaction => database.Coordinator.TryGetStorageTransaction(transaction, out var bracket)
                ? bracket : throw new InvalidOperationException(),
        });
        try
        {
            await database.Coordinator.ApplyStatementAsync(context, async bracket =>
            {
                var tree = await physical.CreateIndexAsync(context, owner, new("baseline"), guard.Token);
                foreach (var node in nodes.OrderBy(node => node.Id))
                {
                    foreach (var key in GraphTextTrigrams.Keys(value, guard.Token))
                    {
                        var bytes = new byte[key.Length + 8];
                        key.Encoded.Span.CopyTo(bytes);
                        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(key.Length), node.Id);
                        await tree.InsertVersionAsync(bracket, new(bytes), node.Location, node.Writer, TransactionSequence.None, guard.Token);
                    }
                }
                return true;
            }, durable: true, cancellationToken: guard.Token);
        }
        finally { await database.Coordinator.RollbackAsync(context, guard.Token); }
        database.Coordinator.Checkpoint(guard.Token);
        peaks[0] = Peak(0);
        while ((database.DataStorage.GetOwnerIndexPageCount(owner) != 0))
        {
            using var admission = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("BaselineCleanup", cancellationToken: guard.Token);
            using var bracket = database.DataStorage.BeginTransaction();
            database.DataStorage.FreeOwnerPages(bracket, owner, 64);
            bracket.Commit();
        }
        database.Coordinator.Checkpoint(guard.Token);
        int baselineEvents = events.Events.Count;
        using var lifecycle = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Informational);
        int entries = 0, previous = 0, chunks = 0;
        long chunkStart = 0, committedLength = 0;
        bool inChunk = false;
        var chunkCommits = new List<long>();
        var deltas = new List<long>();
        FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () =>
        {
            // Commit durability runs while the physical bracket is active.
            // Gate-release checkpoint flushes run after it leaves the active set.
            if (!inChunk || !database.DataStorage.HasActiveTransactions) { return; }
            long length = database.DataStorage.JournalLength;
            committedLength = Math.Max(committedLength, length);
        };
        database.Store.AfterTextIndexMutation = stage =>
        {
            if (stage == "build-chunk-start") { chunkStart = database.DataStorage.JournalLength; committedLength = chunkStart; inChunk = true; }
            if (stage == "build-entry") { entries++; }
            if (stage != "build-chunk") { return; }
            inChunk = false;
            chunkCommits.Add(committedLength); deltas.Add(committedLength - chunkStart);
            (entries - previous).ShouldBeInRange(1, 64);
            previous = entries;
            chunks++;
        };

        // Act.
        try { await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: guard.Token); }
        finally { database.Store.AfterTextIndexMutation = null; FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }
        database.Coordinator.Checkpoint(guard.Token);

        peaks[1] = Peak(baselineEvents);
        // Assert: journal bytes are measured at checkpoint admission, before truncation.
        lifecycle.Events.ShouldNotContain(item => item.EventId == 0);
        lifecycle.Events.Count(item => item.EventId == 16).ShouldBe(1);
        lifecycle.Events.Count(item => item.EventId == 17).ShouldBe(1);
        entries.ShouldBe(8 * 1022);
        chunks.ShouldBe((entries + 63) / 64);
        chunkCommits.Count.ShouldBe(chunks);
        foreach (var checkpoint in events.Events.Skip(baselineEvents).Where(item => item.EventId == 5 && Equals(item.Payload![0], database.DataStorage.Name.ToString())))
        { ((long)checkpoint.Payload![2]!).ShouldBeLessThanOrEqualTo(options.CheckpointJournalSize + deltas.Max()); }
        peaks[0].ShouldBeGreaterThan(0);
        peaks[1].ShouldBeGreaterThan(0);
        if (trigger == 64 * 1024) { peaks[1].ShouldBeLessThan(peaks[0]); }
        else
        {
            deltas.Max().ShouldBeLessThan(trigger);
            chunkCommits.ShouldContain(length => length > deltas.Max(), "at least one checkpoint window must accumulate chunks");
        }
        _output.WriteLine($"TEXT_BUILD_JOURNAL|entries={entries}|whole-bracket={peaks[0]}|chunked={peaks[1]}|chunks={chunks}|largest-chunk-delta={deltas.Max()}|commits={string.Join(',', chunkCommits)}");
    }
}
