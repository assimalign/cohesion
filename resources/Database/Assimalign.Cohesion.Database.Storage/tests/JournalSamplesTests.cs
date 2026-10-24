using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class JournalSamplesTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Journal observation: complete append/truncate cycles cannot disappear (#1285)")]
    public async Task CollectAsync_CyclesBetweenObservations_ShouldCountEveryAppendAndTruncation()
    {
        using var journal = new JournalTrackingStream();
        var writers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await JournalSamples.CollectAsync(() =>
        {
            // Both complete cycles happen before the first observation.
            if (!writers.Task.IsCompleted)
            {
                journal.Write(new byte[2048]);
                journal.SetLength(0);
                journal.Position = 0;
                journal.Write(new byte[2048]);
                journal.SetLength(0);
                writers.SetResult();
            }
            return journal.Counters;
        }, 4096, writers.Task);

        result.Written.ShouldBe(4096);
        result.Truncations.ShouldBe(2);
        result.Largest.ShouldBe(2048);
    }
}
