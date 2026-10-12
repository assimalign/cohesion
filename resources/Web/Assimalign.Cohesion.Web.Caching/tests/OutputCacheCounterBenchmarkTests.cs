using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Internal;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>Opt-in Release benchmark; see DESIGN.md for the two source variants and budget.</summary>
[Collection("Output cache events")]
public sealed class OutputCacheCounterBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public OutputCacheCounterBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - Benchmark: Cacheable GET counter cost")]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 4)]
    [InlineData(false, 4)]
    public async Task InvokeAsync_CacheableGet_ShouldMeasureCounterCost(bool hit, int workers)
    {
        if (Environment.GetEnvironmentVariable("COHESION_CACHE_COUNTER_BENCHMARK") != "1")
        {
            return;
        }

        // Arrange — no listeners, a real GET/200 middleware path and synchronous in-memory I/O.
        OutputCacheEventSource.Log.IsEnabled().ShouldBeFalse();
        const int iterations = 100_000;
        const int rounds = 9;
        double[] nanoseconds = new double[rounds];
        double[] allocatedBytes = new double[rounds];
        await MeasureAsync(hit, workers, 20_000);

        // Act — each batch has dedicated, synchronized workers; setup and thread creation are excluded.
        for (int round = 0; round < rounds; round++)
        {
            (nanoseconds[round], allocatedBytes[round]) = await MeasureAsync(hit, workers, iterations);
        }

        // Assert / report — performance decisions use medians, never a timing assertion in CI.
        nanoseconds.All(value => value > 0).ShouldBeTrue();
        Array.Sort(nanoseconds);
        Array.Sort(allocatedBytes);
        _output.WriteLine($"variant={Environment.GetEnvironmentVariable("COHESION_CACHE_COUNTER_VARIANT")}; path={(hit ? "hit" : "miss")}; workers={workers}; iterations/worker={iterations}; rounds={rounds}; median-ns/op={nanoseconds[rounds / 2]:F3}; bytes/op={allocatedBytes[rounds / 2]:F3}; samples-ns/op={string.Join(",", nanoseconds.Select(value => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}");
    }

    private static async Task<(double Nanoseconds, double Bytes)> MeasureAsync(bool hit, int workers, int iterations)
    {
        using Barrier ready = new(workers);
        Task<(long Ticks, long Bytes)>[] tasks = new Task<(long Ticks, long Bytes)>[workers];
        for (int worker = 0; worker < workers; worker++)
        {
            tasks[worker] = Task.Factory.StartNew(() =>
            {
                OutputCacheOptions options = new();
                options.AddBasePolicy(_ => { });
                OutputCacheMiddleware middleware = new(new BenchmarkStore(hit), options);
                OutputCacheTestContext context = new();
                context.Response.Body = Stream.Null;
                BenchmarkCompletion completion = new();
                context.Features.Set<IWebResponseCompletionFeature>(completion);
                byte[] body = new byte[64];
                WebApplicationMiddleware endpoint = current => current.Response.Body.WriteAsync(body, 0, body.Length, current.RequestCancelled);
                ready.SignalAndWait();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    middleware.InvokeAsync(context, endpoint).GetAwaiter().GetResult();
                    completion.CompleteAsync().GetAwaiter().GetResult();
                }
                long elapsed = Stopwatch.GetTimestamp() - started;
                return (elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        (long Ticks, long Bytes)[] results = await Task.WhenAll(tasks);
        return (results.Max(result => result.Ticks) * (1_000_000_000.0 / Stopwatch.Frequency) / (iterations * workers),
            results.Sum(result => result.Bytes) / (double)(iterations * workers));
    }

    private sealed class BenchmarkCompletion : IWebResponseCompletionFeature
    {
        private Func<ValueTask>? _callback;
        public string Name => nameof(IWebResponseCompletionFeature);
        public void Register(Func<ValueTask> callback) => _callback = callback;
        public ValueTask CompleteAsync()
        {
            Func<ValueTask>? callback = _callback;
            _callback = null;
            return callback?.Invoke() ?? ValueTask.CompletedTask;
        }
    }

    private sealed class BenchmarkStore : IOutputCacheStore
    {
        private readonly OutputCacheEntry? _entry;
        public BenchmarkStore(bool hit)
        {
            _entry = hit ? new OutputCacheEntry(HttpStatusCode.Ok, [], new byte[64], DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(20), [], []) : null;
        }
        public ValueTask<OutputCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(_entry);
        public ValueTask SetAsync(string key, OutputCacheEntry entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
