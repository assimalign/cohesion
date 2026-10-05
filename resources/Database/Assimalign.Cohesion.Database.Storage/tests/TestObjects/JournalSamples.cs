using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A journal's length sampled while writers append to it, for the engines' tests of the
/// checkpoint size trigger (#1254): the bytes appended, the checkpoints that truncated the
/// journal, and the largest length seen.
/// </summary>
/// <param name="Written">The bytes the samples saw appended.</param>
/// <param name="Truncations">The samples shorter than the one before them: the checkpoints that truncated the journal.</param>
/// <param name="Largest">The largest length sampled.</param>
/// <param name="Elapsed">How long the sampling ran.</param>
/// <remarks>
/// <para>
/// The bytes a cycle appends after its last sample before a truncation are not seen, and two
/// truncations between two samples count as one. Both undercount alike, so the journal written per
/// truncation stays the length at which, as far as the samples saw, a checkpoint found it.
/// </para>
/// <para>
/// That holds only while the samples come several times a cycle, so they run on a thread of their
/// own. Sampled from a task, beside four writers that kept the thread pool busy on a three-core
/// machine, they went whole cycles unscheduled: in one key-value run they counted under 160 MiB
/// of journal in a minute while the writers' data file outgrew 2 GiB, because each missed cycle
/// counted only what the journal held at the next sample.
/// </para>
/// </remarks>
public readonly record struct JournalSamples(long Written, int Truncations, long Largest, TimeSpan Elapsed)
{
    /// <summary>
    /// Gets how long the samples may take to see their target: a hang guard, not a throughput
    /// floor. A correct run reaches the target however slow the machine, and the bounds under test
    /// are ratios.
    /// </summary>
    public static TimeSpan HangGuard { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Samples a journal's length about every millisecond, on a thread of its own, until the
    /// samples saw <paramref name="target"/> bytes appended or <paramref name="writers"/> ended.
    /// </summary>
    /// <param name="length">Reads the journal's current length.</param>
    /// <param name="target">The bytes to see appended.</param>
    /// <param name="writers">
    /// The writers appending to the journal. They run until the test stops them, so they end early
    /// only by failing: the samples stop then, and the test's wait for the writers reports why.
    /// </param>
    /// <returns>The samples' summary.</returns>
    /// <exception cref="ShouldAssertException">The samples did not see the target within <see cref="HangGuard"/>.</exception>
    public static Task<JournalSamples> CollectAsync(Func<long> length, long target, Task writers)
    {
        ArgumentNullException.ThrowIfNull(length);
        ArgumentNullException.ThrowIfNull(writers);
        return Task.Factory.StartNew(
            () => Collect(length, target, writers), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static JournalSamples Collect(Func<long> length, long target, Task writers)
    {
        long largest = 0;
        long written = 0;
        long previous = 0;
        int truncations = 0;
        var watch = Stopwatch.StartNew();
        while (written < target && !writers.IsCompleted)
        {
            if (watch.Elapsed > HangGuard)
            {
                throw new ShouldAssertException($"The writers journaled {written:N0} of {target:N0} bytes in {watch.Elapsed.TotalSeconds:F0} s.");
            }

            long current = length();
            largest = Math.Max(largest, current);
            if (current < previous)
            {
                // A checkpoint truncated the journal; what it holds now was appended since.
                truncations++;
                written += current;
            }
            else
            {
                written += current - previous;
            }

            previous = current;
            Thread.Sleep(1);
        }

        return new JournalSamples(written, truncations, largest, watch.Elapsed);
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
