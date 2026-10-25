#if !DEBUG
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>Records warmed lease-exchange cost without an allocation or timing gate.</summary>
[Collection(nameof(DatabaseClientEventSourceCollection))]
public sealed class ClientPingMeasurementTests
{
    private const int sampleCount = 12;
    private const int iterations = 100;
    private readonly ITestOutputHelper _output;

    public ClientPingMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Ping: record validated warmed-rental allocation and duration samples")]
    public async Task ExecuteAsync_WarmedPing_ShouldRecordProcessAllocationAndDurationSamplesAsync()
    {
        // Arrange: one already-open rental and one reusable exchange; setup is unmeasured.
        // This is an asynchronous client/server path, so precise process allocation includes
        // both sides regardless of which thread resumes them. The collection is nonparallel.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = new StreamingClientTestHarness();
        await using var connection = await harness.Client.RentAsync(timeout.Token);
        var exchange = StreamingClientTestHarness.CreatePing();
        var allocatedBytes = new long[sampleCount];
        var elapsedTicks = new long[sampleCount];
        await MeasureAsync(100);

        // Act: retain all twelve samples; every response is checked before continuing.
        for (int sample = 0; sample < sampleCount; sample++)
        {
            (allocatedBytes[sample], elapsedTicks[sample]) = await MeasureAsync(iterations);
        }

        // Assert: the response checks above cover every operation. Aggregates are descriptive.
        var bytesPerPing = new double[sampleCount];
        var nanosecondsPerPing = new double[sampleCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            bytesPerPing[sample] = allocatedBytes[sample] / (double)iterations;
            nanosecondsPerPing[sample] = elapsedTicks[sample] * (1_000_000_000d / Stopwatch.Frequency) / iterations;
            _output.WriteLine(FormattableString.Invariant(
                $"CLIENT_PING_SAMPLE sample={sample + 1} iterations={iterations} allocated_bytes={allocatedBytes[sample]} bytes_per_ping={bytesPerPing[sample]:F4} elapsed_ticks={elapsedTicks[sample]} nanoseconds_per_ping={nanosecondsPerPing[sample]:F4}"));
        }
        double medianBytes = Median(bytesPerPing);
        double medianNanoseconds = Median(nanosecondsPerPing);
        int allocationsAtOrBelowMedian = 0;
        int durationsAtOrBelowMedian = 0;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            if (bytesPerPing[sample] <= medianBytes) { allocationsAtOrBelowMedian++; }
            if (nanosecondsPerPing[sample] <= medianNanoseconds) { durationsAtOrBelowMedian++; }
        }
        _output.WriteLine(FormattableString.Invariant(
            $"CLIENT_PING_SUMMARY samples={sampleCount} iterations={iterations} median_bytes_per_ping={medianBytes:F4} median_nanoseconds_per_ping={medianNanoseconds:F4} allocation_samples_at_or_below_median={allocationsAtOrBelowMedian}/{sampleCount} duration_samples_at_or_below_median={durationsAtOrBelowMedian}/{sampleCount} process_allocation_precise=true allocation_gate=false timing_gate=false"));
        connection.IsOpen.ShouldBeTrue();
        harness.AcceptedConnections.ShouldBe(1);

        async ValueTask<(long AllocatedBytes, long ElapsedTicks)> MeasureAsync(int count)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < count; iteration++)
            {
                ProtocolMessageType result = await connection.ExecuteAsync(exchange, timeout.Token).ConfigureAwait(false);
                if (result != ProtocolMessageType.Pong) { result.ShouldBe(ProtocolMessageType.Pong); }
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            return (GC.GetTotalAllocatedBytes(precise: true) - before, elapsed);
        }
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
#endif
