#if !DEBUG
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>Records the existing scalar query path before and after function dispatch changes.</summary>
[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentFunctionDispatchMeasurementTests
{
    private const int sampleCount = 12;
    private const int queriesPerSample = 1_000;
    private readonly ITestOutputHelper _output;

    public DocumentFunctionDispatchMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: record validated scalar query allocation and elapsed distributions")]
    public async Task Execute_NativeScalarQueries_ShouldReportValidatedMeasurementsAsync()
    {
        // Arrange: the parsed request is reused, so the measurement includes planning,
        // storage reads, six native scalar calls, row consumption and disposal, but not parsing.
        // The existing isolated allocation collection prevents other tests in this process
        // from contaminating the process-wide counter when an await changes threads.
        await using var engine = DocumentDatabaseEngine.Create("function-dispatch-measurement", new());
        var database = await engine.CreateDatabaseAsync("measurement", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", "{\"name\":\"Alpha\",\"amount\":-2,\"tags\":[\"blue\",\"green\"]}"u8.ToArray(),
            cancellationToken: CancellationToken.None);
        var request = DocumentQueryRequest.FromOql(
            "SELECT UPPER(LOWER(name)) AS name, LENGTH(name) AS length, ABS(amount) AS amount, ARRAY_LENGTH(tags) AS count " +
            "FROM items WHERE CONTAINS(name, 'ph')");
        await ExecuteBatchAsync(session, request, queriesPerSample);
        var allocatedBytesPerQuery = new double[sampleCount];
        var elapsedMicrosecondsPerQuery = new double[sampleCount];

        // Act: each sample executes 1,000 real queries and checks every returned value.
        for (int sample = 0; sample < sampleCount; sample++)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            await ExecuteBatchAsync(session, request, queriesPerSample);
            long stopped = Stopwatch.GetTimestamp();
            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            allocatedBytesPerQuery[sample] = (allocatedAfter - allocatedBefore) / (double)queriesPerSample;
            elapsedMicrosecondsPerQuery[sample] = (stopped - started) * 1_000_000d / Stopwatch.Frequency / queriesPerSample;
        }

        // Assert: results, not elapsed time or allocation, decide this measurement test.
        // Report medians and the share within 10% of each median across all twelve samples;
        // these are descriptive measurements, never a timing ratio or budget assertion.
        double medianAllocated = Median(allocatedBytesPerQuery);
        double medianElapsed = Median(elapsedMicrosecondsPerQuery);
        int allocatedNearMedian = allocatedBytesPerQuery.Count(value => value <= medianAllocated * 1.10);
        int elapsedNearMedian = elapsedMicrosecondsPerQuery.Count(value => value <= medianElapsed * 1.10);
        _output.WriteLine(FormattableString.Invariant(
            $"Native scalar dispatch: samples={sampleCount}; queries/sample={queriesPerSample}; native calls/query=6; validated queries={sampleCount * queriesPerSample}."));
        _output.WriteLine(FormattableString.Invariant(
            $"Median allocated bytes/query={medianAllocated:F3}; share <=110% of allocation median={allocatedNearMedian}/{sampleCount}."));
        _output.WriteLine(FormattableString.Invariant(
            $"Median elapsed microseconds/query={medianElapsed:F3}; share <=110% of elapsed median={elapsedNearMedian}/{sampleCount}."));
        _output.WriteLine("Allocated bytes/query samples: " + string.Join(", ", allocatedBytesPerQuery.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))));
        _output.WriteLine("Elapsed microseconds/query samples: " + string.Join(", ", elapsedMicrosecondsPerQuery.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))));
    }

    private static async ValueTask ExecuteBatchAsync(DocumentDatabaseSession session, DocumentQueryRequest request, int queryCount)
    {
        for (int query = 0; query < queryCount; query++)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
            if (result.Diagnostics.Count != 0) { result.Diagnostics.ShouldBeEmpty(); }
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                bool correct = row.GetString(0) == "ALPHA" && row.GetInt64(1) == 5L &&
                    (decimal)row.GetValue(2)! == 2m && row.GetInt64(3) == 2L;
                if (!correct) { correct.ShouldBeTrue("Every measured scalar query must retain its exact result values."); }
                rows++;
            }
            if (rows != 1) { rows.ShouldBe(1, "Every measured scalar query must return exactly one row."); }
        }
    }

    private static double Median(double[] samples)
    {
        double[] ordered = samples.Order().ToArray();
        return (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }
}
#endif
