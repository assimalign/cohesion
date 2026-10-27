#if !DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Records existing built-in query costs before and after function-dispatch changes.</summary>
[Collection(nameof(GraphDatabaseEventSourceCollection))]
public sealed class GraphFunctionDispatchMeasurementTests
{
    private const int sampleCount = 12;
    private const int iterationsPerSample = 1_000;
    private const int warmupQueries = 200;
    private readonly ITestOutputHelper _output;

    /// <summary>Receives the measurement output sink.</summary>
    /// <param name="output">The test's output sink.</param>
    public GraphFunctionDispatchMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Validates real query results and reports descriptive allocation/timing samples.</summary>
    /// <param name="aggregate">Whether to measure grouped aggregate dispatch instead of scalars.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: record validated scalar and aggregate dispatch measurements")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BuiltinQueries_ShouldRecordValidatedDispatchMeasurementsAsync(bool aggregate)
    {
        // Arrange: parsing, engine/session setup, warmup and reporting are outside measurement.
        // Two minutes is a hang guard, not a latency or throughput acceptance threshold.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("function-dispatch-measurement", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        string profile = aggregate ? "aggregate" : "scalar";
        GraphQueryRequest request = GraphQueryRequest.FromGql(aggregate
            ? "FOR v IN [1,2,3,3,NULL] RETURN count(*), count(v), sum(v), avg(v), min(v), max(v), count(DISTINCT v)"
            : "RETURN abs(-7), upper('abc'), char_length('A😀B')");
        object?[] expected = aggregate ? [5L, 4L, 9L, 2.25d, 1L, 3L, 3L] : [7L, "ABC", 3L];
        for (int query = 0; query < warmupQueries; query++)
        {
            ExecuteAndValidate(session, request, expected, timeout.Token);
        }
        var allocatedBytesPerQuery = new double[sampleCount];
        var elapsedMicrosecondsPerQuery = new double[sampleCount];
        var completed = new int[sampleCount];

        // Act: require the existing warmed in-memory path to finish on this thread, so a
        // continuation cannot invalidate GetAllocatedBytesForCurrentThread. No measured value
        // decides success. Assertions run only on unexpected values inside the measured loop.
        for (int sample = 0; sample < sampleCount; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int queries = 0;
            for (int query = 0; query < iterationsPerSample; query++)
            {
                ExecuteAndValidate(session, request, expected, timeout.Token);
                queries++;
            }
            long stop = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            allocatedBytesPerQuery[sample] = allocated / (double)iterationsPerSample;
            elapsedMicrosecondsPerQuery[sample] = Stopwatch.GetElapsedTime(start, stop).TotalMicroseconds / iterationsPerSample;
            completed[sample] = queries;
        }

        // Assert: only execution counts, values, types, single-row shape and synchronous
        // completion/cleanup are guards. Allocations, elapsed medians and shares are descriptive.
        foreach (int queries in completed) { queries.ShouldBe(iterationsPerSample); }
        double medianBytes = Median(allocatedBytesPerQuery);
        double medianMicroseconds = Median(elapsedMicrosecondsPerQuery);
        for (int sample = 0; sample < sampleCount; sample++)
        {
            _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Graph function dispatch {0} sample {1}/{2}: queries={3}; allocated={4:F4} bytes/query; elapsed={5:F4} us/query.",
                profile, sample + 1, sampleCount, completed[sample], allocatedBytesPerQuery[sample], elapsedMicrosecondsPerQuery[sample]));
        }
        _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Graph function dispatch {0}: samples={1}; queries/sample={2}; warmup={3}; median allocated={4:F4} bytes/query; median elapsed={5:F4} us/query; allocation share <= 1.01x median={6}/{1}; elapsed share <= 1.10x median={7}/{1}; no allocation/timing acceptance threshold.",
            profile, sampleCount, iterationsPerSample, warmupQueries, medianBytes, medianMicroseconds,
            ShareWithin(allocatedBytesPerQuery, medianBytes * 1.01), ShareWithin(elapsedMicrosecondsPerQuery, medianMicroseconds * 1.10)));
    }

    private static void ExecuteAndValidate(GraphDatabaseSession session, GraphQueryRequest request,
        object?[] expected, CancellationToken cancellationToken)
    {
        DatabaseResult outcome = Complete(session.ExecuteAsync(request, cancellationToken));
        if (outcome is not DatabaseResultSet rows)
        {
            outcome.ShouldBeAssignableTo<DatabaseResultSet>();
            return;
        }
        try
        {
            if (rows.Status != DatabaseResultStatus.Success) { rows.Status.ShouldBe(DatabaseResultStatus.Success); }
            if (rows.Columns.Count != expected.Length) { rows.Columns.Count.ShouldBe(expected.Length); }
            IAsyncEnumerator<DatabaseRow> stream = rows.GetRowsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                bool hasRow = Complete(stream.MoveNextAsync());
                if (!hasRow) { hasRow.ShouldBeTrue(); }
                DatabaseRow row = stream.Current;
                for (int column = 0; column < expected.Length; column++)
                {
                    object? actual = row.GetValue(column);
                    // Object.Equals retains both exact numeric CLR types and ordinal string equality.
                    if (!Equals(actual, expected[column])) { actual.ShouldBe(expected[column]); }
                }
                bool hasSecondRow = Complete(stream.MoveNextAsync());
                if (hasSecondRow) { hasSecondRow.ShouldBeFalse(); }
            }
            finally
            {
                Complete(stream.DisposeAsync());
            }
        }
        finally
        {
            Complete(rows.DisposeAsync());
        }
    }

    private static T Complete<T>(ValueTask<T> pending)
    {
        if (!pending.IsCompleted)
        {
            pending.IsCompleted.ShouldBeTrue("The measured warmed query must finish on the measuring thread.");
        }
        // A synchronously faulted/cancelled operation propagates its original exception.
        return pending.GetAwaiter().GetResult();
    }

    private static void Complete(ValueTask pending)
    {
        if (!pending.IsCompleted)
        {
            pending.IsCompleted.ShouldBeTrue("The measured warmed query must dispose on the measuring thread.");
        }
        pending.GetAwaiter().GetResult();
    }

    private static double Median(double[] values)
    {
        var ordered = (double[])values.Clone();
        Array.Sort(ordered);
        return (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }

    private static int ShareWithin(double[] values, double ceiling)
    {
        int count = 0;
        foreach (double value in values) { if (value <= ceiling) { count++; } }
        return count;
    }
}
#endif
