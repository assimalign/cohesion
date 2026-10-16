using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>Exposes samples lazily, with optional random lookup and safe range seeking.</summary>
/// <remarks>Memory use for a lookup must not grow with the total sample count. Use native runs,
/// pages, sparse cues, or bounded scans, never a materialized per-sample table as a lookup prerequisite.
/// Enumeration is in decode/source order; presentation timestamps need not be sorted.
/// Operations on a shared source are sequential unless the implementation explicitly supports concurrency.
/// Malformed container data must be translated by the profile to ContentFormatException with its absolute position.</remarks>
public interface IMediaSampleIndex
{
    /// <summary>Gets the nonzero track identifier shared by every returned sample.</summary>
    ulong TrackId { get; }

    /// <summary>Gets the nonnegative sample count if known without scanning the source; otherwise null.</summary>
    long? Count { get; }

    /// <summary>Gets whether random lookup by zero-based sample index is supported.</summary>
    bool CanLookupByIndex { get; }

    /// <summary>Gets whether random lookup by presentation time is supported.</summary>
    bool CanLookupByTime { get; }

    /// <summary>Gets whether safe seek ranges can be resolved over the available source.</summary>
    bool CanSeek { get; }

    /// <summary>Streams samples from the beginning, with no eager sample-table expansion.</summary>
    /// <param name="cancellationToken">A token checked during enumeration.</param>
    /// <returns>The samples. Re-enumeration is not guaranteed for a single-use source.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    IAsyncEnumerable<MediaSample> ReadSamplesAsync(CancellationToken cancellationToken = default);

    /// <summary>Looks up a sample by its zero-based decode/source-order index.</summary>
    /// <param name="index">The nonnegative sample index.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The sample, or null if it is outside the available index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative.</exception>
    /// <exception cref="NotSupportedException">Lookup by index is unavailable.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    ValueTask<MediaSample?> GetSampleAsync(long index, CancellationToken cancellationToken = default);

    /// <summary>Finds the sample with the greatest presentation timestamp no later than the requested time.</summary>
    /// <param name="presentationTime">A time on the file's presentation timeline, in any timescale.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The preceding/equal sample (lowest index breaks ties), or null before the first available
    /// presentation timestamp. After the last timestamp, returns that last sample, even in a gap.</returns>
    /// <exception cref="NotSupportedException">Lookup by time is unavailable.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    ValueTask<MediaSample?> FindSampleAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default);

    /// <summary>Resolves the preceding sync sample and a source range sufficient to decode through the time lookup's target.</summary>
    /// <param name="presentationTime">A time on the file's presentation timeline, in any timescale.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A range and target, or null if there is no target or usable preceding sync sample.
    /// Choose the latest sync presentation time no later than the request whose index does not exceed the
    /// target's index (lowest index breaks ties). The range includes intervening samples, container framing,
    /// and required preroll/dependencies; the profile may conservatively overfetch.</returns>
    /// <remarks>The file's initialization metadata and codec data are assumed available. If the profile cannot
    /// establish a safe range for this query (including configuration changes), it must throw NotSupportedException.
    /// Never substitute the sync sample's payload alone for a decode range.</remarks>
    /// <exception cref="NotSupportedException">Seeking is unavailable or a safe range cannot be established.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    ValueTask<MediaSeekResult?> SeekAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default);
}
