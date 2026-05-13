using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Assimalign.Cohesion.Caching;
using Assimalign.Cohesion.Caching.InMemory;

namespace Assimalign.Cohesion.Caching.InMemory.Tests;

/// <summary>
/// Coarse performance baselines for the in-memory cache.
/// </summary>
/// <remarks>
/// <para>
/// These tests are intentionally not microbenchmarks. They run a representative workload and
/// confirm the operation completes well inside a generous budget so an accidental regression
/// (an O(n) hot path slipping into a loop, a missing lock-free path) trips CI rather than
/// going unnoticed.
/// </para>
/// <para>
/// The thresholds were chosen so that any modern CI worker (Linux/Windows/macOS GitHub-hosted
/// runners) clears them with significant headroom. Tightening the thresholds would buy
/// nothing - we just want a watchdog, not a benchmark.
/// </para>
/// </remarks>
public class MemoryCachePerformanceBaselineTests
{
    private const int HotIterations = 100_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Baseline: 100k Set operations complete inside budget")]
    public void Set_HotPath_StaysInsideBudget()
    {
        using var cache = new MemoryCache();
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < HotIterations; i++)
        {
            cache.Set(i, i);
        }

        sw.Stop();
        Assert.True(
            sw.Elapsed < Budget,
            $"Set hot path took {sw.Elapsed}, budget {Budget}.");
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Baseline: 100k TryGetValue lookups complete inside budget")]
    public void TryGetValue_HotPath_StaysInsideBudget()
    {
        using var cache = new MemoryCache();
        for (int i = 0; i < HotIterations; i++)
        {
            cache.Set(i, i);
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < HotIterations; i++)
        {
            cache.TryGetValue(i, out _);
        }

        sw.Stop();
        Assert.True(
            sw.Elapsed < Budget,
            $"TryGetValue hot path took {sw.Elapsed}, budget {Budget}.");
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Baseline: 50k GetOrCreate operations complete inside budget")]
    public void GetOrCreate_HotPath_StaysInsideBudget()
    {
        using var cache = new MemoryCache();
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < HotIterations / 2; i++)
        {
            cache.GetOrCreate(i, _ => i.ToString());
        }

        sw.Stop();
        Assert.True(
            sw.Elapsed < Budget,
            $"GetOrCreate hot path took {sw.Elapsed}, budget {Budget}.");
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Baseline: parallel hot reads on a small working set scale")]
    public async Task ParallelReads_SmallWorkingSet_StayInsideBudget()
    {
        using var cache = new MemoryCache();
        const int WorkingSet = 1024;
        const int ReadsPerWorker = 20_000;

        for (int i = 0; i < WorkingSet; i++)
        {
            cache.Set(i, i);
        }

        var sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Workers(Environment.ProcessorCount),
            (_, _) =>
            {
                for (int i = 0; i < ReadsPerWorker; i++)
                {
                    cache.TryGetValue(i & (WorkingSet - 1), out _);
                }
                return ValueTask.CompletedTask;
            });
        sw.Stop();

        Assert.True(
            sw.Elapsed < Budget,
            $"Parallel reads took {sw.Elapsed}, budget {Budget}.");
    }

    private static System.Collections.Generic.IEnumerable<int> Workers(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return i;
        }
    }
}
