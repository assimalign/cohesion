using System;
using System.Diagnostics;
using System.Threading.Tasks;

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
/// The bytes a cycle appends after its last sample before a truncation are not seen, and two
/// truncations between two samples count as one. Both undercount alike, so the journal written per
/// truncation stays the length at which, as far as the samples saw, a checkpoint found it.
/// </remarks>
public readonly record struct JournalSamples(long Written, int Truncations, long Largest, TimeSpan Elapsed)
{
    /// <summary>
    /// Samples a journal's length about every millisecond until the samples saw
    /// <paramref name="target"/> bytes appended, or a minute passed.
    /// </summary>
    /// <param name="length">Reads the journal's current length.</param>
    /// <param name="target">The bytes to see appended.</param>
    /// <returns>The samples' summary.</returns>
    public static async Task<JournalSamples> CollectAsync(Func<long> length, long target)
    {
        ArgumentNullException.ThrowIfNull(length);

        long largest = 0;
        long written = 0;
        long previous = 0;
        int truncations = 0;
        var watch = Stopwatch.StartNew();
        while (written < target && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
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
            await Task.Delay(1);
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
