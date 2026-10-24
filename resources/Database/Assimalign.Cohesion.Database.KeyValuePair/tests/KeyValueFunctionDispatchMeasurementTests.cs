#if !DEBUG
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>Records the existing read-command dispatch cost before adding function requests.</summary>
[Collection(nameof(KeyValueDatabaseEventSourceCollection))]
public sealed class KeyValueFunctionDispatchMeasurementTests
{
    private const int sampleCount = 12;
    private const int iterations = 5_000;
    private readonly ITestOutputHelper _output;

    public KeyValueFunctionDispatchMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Function dispatch: record existing read-command cost without a timing gate")]
    [InlineData("GET")]
    [InlineData("EXISTS")]
    public async Task ExecuteAsync_ExistingReadCommand_ShouldRecordValidatedDispatchSamplesAsync(string operation)
    {
        // Arrange: an already-seeded, in-memory point read under one snapshot transaction.
        // Requests and setup are outside measurement. Synchronous completion keeps each
        // allocation interval on one thread; worker allocations do not enter the counter.
        await using var engine = KeyValueDatabaseEngine.Create("function-dispatch-measurement", new()
        {
            CheckpointInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        byte[] key = [1, 2, 3, 4];
        byte[] value = [9, 8, 7, 6, 5];
        var outcome = await database.PutAsync(session, key, value, cancellationToken: CancellationToken.None);
        outcome.Applied.ShouldBeTrue();
        outcome.ETag.ShouldNotBeNull();
        long etag = outcome.ETag.GetValueOrDefault();
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        KeyValueRequest request = operation == "GET" ? new KeyValueGetRequest(key) : new KeyValueExistsRequest(key);
        var allocatedBytes = new long[sampleCount];
        var elapsedTicks = new long[sampleCount];
        Measure(1_000);

        // Act: retain all twelve samples; neither an individual duration nor a ratio is a gate.
        for (int sample = 0; sample < sampleCount; sample++)
        {
            (allocatedBytes[sample], elapsedTicks[sample]) = Measure(iterations);
        }

        // Assert: every measured operation validated its exact returned row and completion.
        // The aggregate median and share describe the samples; they decide no timing assertion.
        double[] bytesPerQuery = new double[sampleCount];
        double[] nanosecondsPerQuery = new double[sampleCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            bytesPerQuery[sample] = allocatedBytes[sample] / (double)iterations;
            nanosecondsPerQuery[sample] = elapsedTicks[sample] * (1_000_000_000d / Stopwatch.Frequency) / iterations;
            _output.WriteLine(FormattableString.Invariant(
                $"KV_DISPATCH_SAMPLE operation={operation} sample={sample + 1} iterations={iterations} allocated_bytes={allocatedBytes[sample]} bytes_per_query={bytesPerQuery[sample]:F4} elapsed_ticks={elapsedTicks[sample]} nanoseconds_per_query={nanosecondsPerQuery[sample]:F4}"));
        }
        double medianBytes = Median(bytesPerQuery);
        double medianNanoseconds = Median(nanosecondsPerQuery);
        int atOrBelowMedian = 0;
        foreach (double sample in nanosecondsPerQuery)
        {
            if (sample <= medianNanoseconds) { atOrBelowMedian++; }
        }
        _output.WriteLine(FormattableString.Invariant(
            $"KV_DISPATCH_SUMMARY operation={operation} samples={sampleCount} iterations={iterations} median_bytes_per_query={medianBytes:F4} median_nanoseconds_per_query={medianNanoseconds:F4} samples_at_or_below_median={atOrBelowMedian}/{sampleCount} timing_gate=false"));

        (long AllocatedBytes, long ElapsedTicks) Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < count; iteration++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                bool commandCompletedSynchronously = pending.IsCompletedSuccessfully;
                DatabaseResult result = commandCompletedSynchronously
                    ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
                if (result is not DatabaseResultSet)
                {
                    result.ShouldBeOfType<DatabaseResultSet>("The measured command must return its query result.");
                }
                var rows = (DatabaseResultSet)result;
                try
                {
                    if (!commandCompletedSynchronously)
                    {
                        commandCompletedSynchronously.ShouldBeTrue("The measured command must complete synchronously.");
                    }
                    if (rows.Status != DatabaseResultStatus.Success || rows.AffectedCount != -1 || rows.Diagnostics.Count != 0)
                    {
                        rows.Status.ShouldBe(DatabaseResultStatus.Success);
                        rows.AffectedCount.ShouldBe(-1);
                        rows.Diagnostics.ShouldBeEmpty();
                    }
                    var cursor = rows.GetRowsAsync(CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);
                    try
                    {
                        bool moved = SynchronousResult(cursor.MoveNextAsync(), "The measured point read must return its row synchronously.");
                        if (!moved) { moved.ShouldBeTrue("The measured point read must return one row."); }
                        DatabaseRow row = cursor.Current;
                        if (operation == "GET")
                        {
                            if (row.FieldCount != 3 || !row.GetBytes(0).Span.SequenceEqual(key)
                                || !row.GetBytes(1).Span.SequenceEqual(value) || row.GetInt64(2) != etag)
                            {
                                row.FieldCount.ShouldBe(3);
                                row.GetBytes(0).Span.SequenceEqual(key).ShouldBeTrue();
                                row.GetBytes(1).Span.SequenceEqual(value).ShouldBeTrue();
                                row.GetInt64(2).ShouldBe(etag);
                            }
                        }
                        else if (row.FieldCount != 1 || !row.GetBoolean(0))
                        {
                            row.FieldCount.ShouldBe(1);
                            row.GetBoolean(0).ShouldBeTrue();
                        }
                        moved = SynchronousResult(cursor.MoveNextAsync(), "The measured point read must finish synchronously.");
                        if (moved) { moved.ShouldBeFalse("The measured point read must end after one row."); }
                    }
                    finally
                    {
                        SynchronousDisposal(cursor.DisposeAsync(), "The measured row cursor must dispose synchronously.");
                    }
                }
                finally
                {
                    SynchronousDisposal(rows.DisposeAsync(), "The measured result must dispose synchronously.");
                }
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            return (GC.GetAllocatedBytesForCurrentThread() - before, elapsed);
        }
    }

    private static T SynchronousResult<T>(ValueTask<T> pending, string because)
    {
        if (pending.IsCompletedSuccessfully) { return pending.Result; }
        // Drain an unexpected asynchronous operation before the enclosing finally disposes
        // its cursor. A real operation failure still propagates with cleanup guaranteed.
        bool completedSynchronously = false;
        T result = pending.AsTask().GetAwaiter().GetResult();
        completedSynchronously.ShouldBeTrue(because);
        return result;
    }

    private static void SynchronousDisposal(ValueTask pending, string because)
    {
        if (pending.IsCompletedSuccessfully)
        {
            pending.GetAwaiter().GetResult();
            return;
        }
        // Finish disposal even when it is unexpectedly asynchronous; then report the
        // measurement-method violation without leaving an outstanding disposal behind.
        bool completedSynchronously = false;
        pending.AsTask().GetAwaiter().GetResult();
        completedSynchronously.ShouldBeTrue(because);
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
#endif
