using System;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectWideMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: all-distinct payloads include exactly one 64-byte identity entry (#1776)")]
    [InlineData(1024, 57065, 67108440L)]
    [InlineData(1048576, 63, 66069864L)]
    public void Add_AllDistinctWideValues_ShouldPinDefaultCapacity(int length, int fits, long expectedPeak)
    {
        // Arrange: a fresh binary object per row is real payload, not a repeated alias.
        var metrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector([], null, null, null, CancellationToken.None, metrics, 67108864);

        // Act
        for (int i = 0; i < fits; i++) { collector.Add([new byte[length]], []); }
        var error = Should.Throw<DatabaseException>(() => collector.Add([new byte[length]], []));

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CS05067");
        collector.RetainedCount.ShouldBe(fits);
        metrics.PeakHashMemoryBytes.ShouldBe(expectedPeak);
        collector.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
        _output.WriteLine($"DISTINCT_WIDE_CAPACITY|length={length}|rows={fits}|peak={expectedPeak}");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: fresh narrow ordered buffers allocate exactly as the uncharged control (#1776)")]
    public void Add_FreshNarrowOrderedBuffer_ShouldAllocateNoDictionary()
    {
        // Arrange: unlike a warmed heap, this includes the first admission and map creation.
        var metrics = new SqlStatementMetrics();
        object?[] row = [1];
        Measure(0); Measure(67108864);

        // Act
        long control = long.MaxValue, charged = long.MaxValue;
        for (int round = 0; round < 3; round++) { control = Math.Min(control, Measure(0)); charged = Math.Min(charged, Measure(67108864)); }

        // Assert
        charged.ShouldBe(control);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
        _output.WriteLine($"FRESH_NARROW_ORDERED_ALLOCATION|control={control}|charged={charged}|operations=1000");

        long Measure(long budget)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                using var collector = Collector(metrics, null, budget);
                collector.Add(row, row);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: last-holder heap replacements reuse map capacity without allocation (#1776)")]
    public void Add_WideHeapReplacement_ShouldReuseEmptyMapCapacity()
    {
        // Arrange: every incoming key replaces the root; two prebuilt wide identities
        // alternate, so each replacement removes the last holder before adding one.
        string[] values = [new string('a', 113), new string('b', 113)];
        var output = new object? [2048][];
        var keys = new object? [2048][];
        for (int i = 0; i < output.Length; i++) { output[i] = [values[i & 1]]; keys[i] = [output.Length - i]; }
        Measure();

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure()); }

        // Assert
        minimum.ShouldBe(0);
        _output.WriteLine($"WIDE_HEAP_REPLACEMENT_ALLOCATION|{minimum}|operations=2048");

        long Measure()
        {
            var metrics = new SqlStatementMetrics();
            using var collector = Collector(metrics, 1);
            collector.Add([values[0]], [int.MaxValue]);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < output.Length; i++) { collector.Add(output[i], keys[i]); }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            metrics.PeakHashMemoryBytes.ShouldBe(448);
            collector.Dispose();
            metrics.HashMemoryBytes.ShouldBe(0);
            metrics.PlainSelectMemoryBytes.ShouldBe(0);
            return bytes;
        }
    }
}
