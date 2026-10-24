using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>Exact journal work observed while the writers run.</summary>
/// <param name="Written">Bytes appended at the stream's successful write points.</param>
/// <param name="Truncations">Length reductions performed by checkpoints.</param>
/// <param name="Largest">Largest length observed at a write or length change.</param>
/// <param name="Elapsed">Elapsed observation time, reported without a performance assertion.</param>
internal readonly record struct JournalSamples(long Written, int Truncations, long Largest, TimeSpan Elapsed)
{
    /// <summary>
    /// Gets how long the samples may take to see their target: a hang guard, not a throughput
    /// floor. A correct run reaches the target however slow the machine, and the bounds under test
    /// are ratios.
    /// </summary>
    public static TimeSpan HangGuard { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Observes exact cumulative counters until the target is reached or the writers complete.
    /// Writers also check this target at operation boundaries, bounding the workload independently
    /// of this observer's scheduling. The five-minute deadline is solely a hang guard.
    /// </summary>
    internal static Task<JournalSamples> CollectAsync(Func<JournalCounters> counters, long target, Task writers)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(writers);
        return DedicatedTestTask.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            JournalCounters current;
            while ((current = counters()).Written < target && !writers.IsCompleted)
            {
                if (watch.Elapsed > HangGuard)
                {
                    throw new ShouldAssertException($"The writers journaled {current.Written:N0} of {target:N0} bytes in {watch.Elapsed.TotalSeconds:F0} s.");
                }
                Thread.Sleep(1);
            }
            // Completion can race the observation; counters retain every intervening cycle.
            current = counters();
            return new JournalSamples(current.Written, current.Truncations, current.Largest, watch.Elapsed);
        });
    }
    /// <summary>
    /// Gets the journal appended per truncation, in multiples of <paramref name="size"/>: the
    /// length at which, on average, a checkpoint truncated it. With no truncation it is everything
    /// appended.
    /// </summary>
    /// <param name="size">The journal size that triggers a checkpoint.</param>
    /// <returns>The journal appended per truncation, in sizes.</returns>
    public double WrittenPerTruncation(long size) => (double)Written / Math.Max(Truncations, 1) / size;

    /// <summary>Describes the samples for an assertion message.</summary>
    /// <param name="size">The journal size that triggers a checkpoint.</param>
    /// <returns>The description.</returns>
    public string Describe(long size)
        => $"written {Written} bytes in {Elapsed}, {Truncations} truncations ({WrittenPerTruncation(size):F2} sizes written per truncation), " +
           $"largest journal {Largest} bytes ({(double)Largest / size:F2} sizes) for a size of {size}";
}
