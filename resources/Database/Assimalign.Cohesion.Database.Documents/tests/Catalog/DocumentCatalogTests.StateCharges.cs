using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{

    [Theory(DisplayName = "Cohesion Test [Documents] - Build charges cover marginal forced-GC retained state at two populations (#1808)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateIndexAsync_RetainedState_ShouldCoverMeasuredMarginalHeap(bool array)
    {
        // Arrange: preserve only real build-owned objects, then measure their release
        // after execution. Catalog and storage caches remain resident on both sides.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var populations = new List<Dictionary<string, (long Heap, long Charge)>>();
        foreach (int population in array ? new[] { 1000, 10000 } : new[] { 8, 16 })
        {
            var rounds = new Dictionary<string, List<(long Heap, long Charge)>>();
            for (int round = 0; round < 64; round++)
            {
                await using var fixture = await Fixture.Create();
                string json = array ? "{\"arr\":[" + string.Join(',', Enumerable.Range(0, population)) + "]}" : "{\"v\":1}";
                for (int row = 0; row < (array ? 1 : population); row++)
                { await fixture.Write(row.ToString("D10"), json); }
                // Act.
                var states = CaptureBuildState(fixture, array, guard.Token);
                if (!array) { VerifyPartialChunk(states); }

                // Assert: shared keys remain held until both chunk and extraction release.
                foreach (string phase in new[] { "names", "documents", "chunk", "keys" })
                {
                    long ownedBytes = MeasureOwnedState(states, phase);
                    long before = LiveHeap.Read();
                    long charged = Release(states, phase);
                    long live = before - LiveHeap.Read();
                    // A collection can also retire unrelated runtime state. Match an
                    // independent copy of the owned graph before accepting a sample;
                    // this control never reads the charge being checked.
                    if (live != ownedBytes) { continue; }
                    if (!rounds.TryGetValue(phase, out var samples)) { rounds.Add(phase, samples = []); }
                    samples.Add((live, charged));
                }
                states.ShouldBeEmpty();
                GC.KeepAlive(fixture);
                if (rounds.Count == 4 && rounds.Values.All(samples => samples.Count >= 5)) { break; }
            }
            rounds.Values.ShouldAllBe(samples => samples.Count >= 5);
            foreach (var phase in rounds)
            { _output.WriteLine($"{phase.Key}: population={population}; positive samples={string.Join(',', phase.Value.Select(sample => sample.Heap))}"); }
            var measured = rounds.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(sample => sample.Heap).ElementAt(pair.Value.Count / 2));
            measured.Keys.Order().ShouldBe(new[] { "chunk", "documents", "keys", "names" });
            foreach (var sample in measured)
            {
                _output.WriteLine($"{sample.Key}: population={population}; median positive retained live={sample.Value.Heap}; charge={sample.Value.Charge}");
                sample.Value.Heap.ShouldBeLessThanOrEqualTo(sample.Value.Charge);
            }
            populations.Add(measured);
        }
        foreach (string phase in populations[0].Keys)
        {
            long slope = populations[1][phase].Heap - populations[0][phase].Heap;
            long chargedSlope = populations[1][phase].Charge - populations[0][phase].Charge;
            _output.WriteLine($"{phase}: marginal population slope={slope}; charge slope={chargedSlope}");
            slope.ShouldBeLessThanOrEqualTo(chargedSlope);
        }
    }

    // Copy exactly the objects released by each phase, using this runtime's real
    // object sizes. Catalog strings/references remain resident; chunk payloads
    // shared with the captured key set remain resident until that set is released.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureOwnedState(Dictionary<string, (object State, long Charge)> states, string phase)
    {
        if (states[phase].State is HashSet<IndexKey> keys)
        {
            foreach (var key in keys) { KeyArray(key); }
            int capacity = keys.EnsureCapacity(0);
            long beforeKeys = GC.GetAllocatedBytesForCurrentThread();
            var copy = new HashSet<IndexKey>(capacity);
            foreach (var key in keys) { copy.Add(new IndexKey(key.Encoded.ToArray())); }
            long allocatedKeys = GC.GetAllocatedBytesForCurrentThread() - beforeKeys;
            GC.KeepAlive(copy);
            return allocatedKeys;
        }
        var source = (Array)states[phase].State;
        var ids = new List<string>();
        var payloads = new List<byte[]>();
        if (phase == "documents")
        {
            foreach (var item in source)
            { ids.Add(((DocumentCatalogEntry)((ITuple)item)[0]!).Id); }
        }
        else if (phase == "chunk")
        {
            var shared = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
            foreach (var key in (HashSet<IndexKey>)states["keys"].State) { shared.Add(KeyArray(key)); }
            foreach (var item in source)
            {
                var bytes = KeyArray((IndexKey)((ITuple)item)[1]!);
                if (shared.Add(bytes)) { payloads.Add(bytes); }
            }
        }
        var pins = new object[ids.Count + payloads.Count];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var arrayCopy = source.Clone();
        int at = 0;
        foreach (string id in ids) { pins[at++] = new string(id.AsSpan()); }
        foreach (var bytes in payloads) { pins[at++] = bytes.Clone(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(arrayCopy);
        GC.KeepAlive(pins);
        return allocated;
    }

    private static byte[] KeyArray(IndexKey key)
    {
        MemoryMarshal.TryGetArray(key.Encoded, out var segment).ShouldBeTrue();
        segment.Offset.ShouldBe(0);
        segment.Count.ShouldBe(segment.Array!.Length);
        return segment.Array!;
    }

    // Finish the build on this stack so Debug cannot retain its async frames
    // in the measuring state machine.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, (object State, long Charge)> CaptureBuildState(
        Fixture fixture, bool array, CancellationToken token)
    {
        var states = new Dictionary<string, (object State, long Charge)>();
        fixture.Catalog.IndexBuildStateCaptured = (phase, state, charge) => states.TryAdd(phase, (state, charge));
        var context = fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, token).AsTask().GetAwaiter().GetResult();
        try
        {
            fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "measured",
                [array ? DocumentIndexKey.DistinctArray("arr") : DocumentIndexKey.Scalar("v")], false, false, context, token)
                .AsTask().GetAwaiter().GetResult();
            fixture.Coordinator.CommitAsync(context, token).AsTask().GetAwaiter().GetResult();
            return states;
        }
        finally { fixture.Catalog.IndexBuildStateCaptured = null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyPartialChunk(Dictionary<string, (object State, long Charge)> states)
    {
        ((Array)states["chunk"].State).Length.ShouldBeLessThan(64);
        states["chunk"].Charge.ShouldBeGreaterThanOrEqualTo(80L + 64L * 24L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Wide-object builds skip revalidation and the DOM fits its charge (#1808)")]
    [InlineData(100000)]
    [InlineData(700000)]
    public async Task CreateIndexAsync_WideObject_ShouldKeepWorkspaceWithinGrant(int names)
    {
        // Arrange: caller-owned input remains resident across both heap samples.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var fixture = await Fixture.Create();
        string json = "{\"v\":1," + string.Join(',', Enumerable.Range(0, names).Select(i => $"\"k{i:D6}\":0")) + "}";
        fixture.Catalog.IndexBuildMemoryBudget = 64L * 1024 * 1024;
        int validations = 0;
        fixture.Storage.BeforeContentValidation = () => validations++;
        byte[] content = Encoding.UTF8.GetBytes(json);
        var metadata = new DocumentIndexMetadata(fixture.Collection.Id, "dom", [DocumentIndexKey.Scalar("v")], false, false, 1000);
        var (heap, charge, allocated) = CaptureDomState(content, metadata, guard.Token);


        // Act.
        await fixture.Write("wide", json);
        await fixture.CreateIndex("wide", "v");

        // Assert.
        _output.WriteLine($"Wide object: names={names}; DOM LiveHeap={heap}; DOM charge={charge}; thread allocations={allocated}; content={content.Length}; grant={fixture.Catalog.IndexBuildMemoryBudget}");
        validations.ShouldBe(0, "stored build content was validated when written");
        allocated.ShouldBeLessThanOrEqualTo(charge);
        allocated.ShouldBeGreaterThan(content.Length);
        GC.KeepAlive(content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Heap, long Charge, long Allocated) CaptureDomState(byte[] content, DocumentIndexMetadata metadata, CancellationToken token)
    {
        long before = 0, measured = 0, charge = 0, allocatedBefore = 0, allocated = 0;
        Action<long> reserve = bytes => charge += bytes;
        Action<object> observe = _ => { allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore; measured = LiveHeap.Read() - before; };
        // Warm the parser, delegate and heap observer without renting the wide
        // document's metadata buckets. Those buffers must remain cold for the sample.
        byte[] warmup = Encoding.UTF8.GetBytes("{\"v\":1," + string.Join(',', Enumerable.Range(0, 1000).Select(i => $"\"k{i}\":0")) + "}");
        for (int round = 0; round < 16; round++)
        { DocumentIndexKeys.Read(warmup, metadata, out _, token, reserve, observe); }
        // Hold pooled buffers until a fresh allocation proves each metadata bucket
        // was drained. Earlier tests may have populated several shared partitions;
        // a fixed number of rentals would still depend on their order.
        var poolPins = new List<byte[]>();
        for (int size = 1024 * 1024; size <= 32 * 1024 * 1024; size *= 2)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                long allocationStart = GC.GetAllocatedBytesForCurrentThread();
                var pin = ArrayPool<byte>.Shared.Rent(size);
                long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                poolPins.Add(pin);
                if (allocatedBytes >= size) { break; }
            }
        }
        charge = 0;
        before = LiveHeap.Read();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        DocumentIndexKeys.Read(content, metadata, out _, token, reserve, observe);
        foreach (var pin in poolPins) { ArrayPool<byte>.Shared.Return(pin); }
        GC.KeepAlive(poolPins);
        GC.KeepAlive(warmup);
        return (measured, charge, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Release(Dictionary<string, (object State, long Charge)> states, string phase)
    {
        var state = states[phase];
        states.Remove(phase);
        return state.Charge;
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNIQUE cursor charges actual bounded key sizes under a small grant (#1808)")]
    [InlineData(1)]
    [InlineData(64)]
    public async Task CreateIndexAsync_OneLongUniqueKey_ShouldFitSmallGrant(int population)
    {
        // Arrange: one long key and otherwise NULL keys fit a bounded cursor batch.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = await Fixture.Create();
        await fixture.Write("000", "{\"v\":\"" + new string('x', 200) + "\"}");
        for (int row = 1; row < population; row++)
        { await fixture.Write(row.ToString("D3"), "{\"v\":null}"); }
        fixture.Catalog.IndexBuildMemoryBudget = 16 * 1024;
        var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);

        // Act.
        await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "unique", ["v"], true, context, guard.Token);
        await fixture.Coordinator.CommitAsync(context, guard.Token);

        // Assert.
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
        fixture.Catalog.GetIndexes(fixture.Collection.Id, reader.Snapshot).ShouldHaveSingleItem().IsUnique.ShouldBeTrue();
        await fixture.Coordinator.CommitAsync(reader, guard.Token);
    }
}
