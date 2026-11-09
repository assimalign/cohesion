using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;
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
        foreach (int population in array ? new[] { 1000, 10000 } : new[] { 8, 16 })
        {
            var minima = new Dictionary<string, (long Heap, long Charge)>();
            for (int round = 0; round < 4; round++)
            {
                await using var fixture = await Fixture.Create();
                string json = array ? "{\"arr\":[" + string.Join(',', Enumerable.Range(0, population)) + "]}" : "{\"v\":1}";
                for (int row = 0; row < (array ? 1 : population); row++)
                { await fixture.Write(row.ToString("D10"), json); }
                var states = new Dictionary<string, (object State, long Charge)>();
                fixture.Catalog.IndexBuildStateCaptured = (phase, state, charge) => states.TryAdd(phase, (state, charge));

                // Act.
                var context = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, guard.Token);
                try
                {
                    await fixture.Catalog.CreateIndexAsync(fixture.Collection.Id, "measured",
                        [array ? DocumentIndexKey.DistinctArray("arr") : DocumentIndexKey.Scalar("v")], false, false, context, guard.Token);
                    await fixture.Coordinator.CommitAsync(context, guard.Token);
                }
                finally { fixture.Catalog.IndexBuildStateCaptured = null; }

                // Assert: shared keys remain held until both chunk and extraction release.
                foreach (string phase in new[] { "names", "documents", "chunk", "keys" })
                {
                    long before = LiveHeap.Read();
                    long charged = Release(states, phase);
                    long live = Math.Max(0, before - LiveHeap.Read());
                    if (!minima.TryGetValue(phase, out var prior) || live < prior.Heap) { minima[phase] = (live, charged); }
                }
                states.ShouldBeEmpty();
                GC.KeepAlive(fixture);
            }
            foreach (var sample in minima)
            {
                _output.WriteLine($"{sample.Key}: population={population}; minimum marginal live={sample.Value.Heap}; charge={sample.Value.Charge}");
                sample.Value.Heap.ShouldBeLessThanOrEqualTo(sample.Value.Charge);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Release(Dictionary<string, (object State, long Charge)> states, string phase)
    {
        var state = states[phase];
        states.Remove(phase);
        return state.Charge;
    }
}
