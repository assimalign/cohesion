using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>Guides profile indexes with cancellation, argument checks, and opt-in random-access operations.</summary>
/// <remarks>There is deliberately no enumeration-based fallback for random lookup or seek. Override only
/// supported capabilities and their corresponding core methods; the interface's lazy memory rule still applies.</remarks>
public abstract class MediaSampleIndex : IMediaSampleIndex
{
    /// <summary>Initializes a profile-owned index.</summary>
    protected MediaSampleIndex() { }

    /// <inheritdoc/>
    public abstract ulong TrackId { get; }

    /// <inheritdoc/>
    public abstract long? Count { get; }

    /// <inheritdoc/>
    public virtual bool CanLookupByIndex => false;

    /// <inheritdoc/>
    public virtual bool CanLookupByTime => false;

    /// <inheritdoc/>
    public virtual bool CanSeek => false;

    /// <inheritdoc/>
    public async IAsyncEnumerable<MediaSample> ReadSamplesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await foreach (var sample in ReadSamplesCoreAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return sample;
        }
    }

    /// <inheritdoc/>
    public ValueTask<MediaSample?> GetSampleAsync(long index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (!CanLookupByIndex)
        {
            throw new NotSupportedException(ErrorMessages.IndexLookupUnavailable);
        }
        return GetSampleCoreAsync(index, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<MediaSample?> FindSampleAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanLookupByTime)
        {
            throw new NotSupportedException(ErrorMessages.TimeLookupUnavailable);
        }
        return FindSampleCoreAsync(presentationTime, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<MediaSeekResult?> SeekAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanSeek)
        {
            throw new NotSupportedException(ErrorMessages.SeekUnavailable);
        }
        return SeekCoreAsync(presentationTime, cancellationToken);
    }

    /// <summary>Streams samples without expanding the whole index.</summary>
    /// <param name="cancellationToken">The enumeration's cancellation token; check it during I/O and scans.</param>
    /// <returns>The lazy sample sequence in decode/source order.</returns>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
    protected abstract IAsyncEnumerable<MediaSample> ReadSamplesCoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Implements supported random lookup by index.</summary>
    /// <param name="index">The validated nonnegative index.</param>
    /// <param name="cancellationToken">A token checked before dispatch; keep checking it during I/O and scans.</param>
    /// <returns>The sample, or null outside the available index.</returns>
    /// <exception cref="NotSupportedException">The operation has not been implemented.</exception>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
    protected virtual ValueTask<MediaSample?> GetSampleCoreAsync(long index, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.IndexLookupUnavailable);

    /// <summary>Implements supported lookup by effective presentation time, including reordering and timeline mappings.</summary>
    /// <param name="presentationTime">The requested time in any timescale.</param>
    /// <param name="cancellationToken">A token checked before dispatch; keep checking it during I/O and scans.</param>
    /// <returns>The preceding/equal sample, or null before the first available presentation timestamp.</returns>
    /// <exception cref="NotSupportedException">The operation has not been implemented.</exception>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
    protected virtual ValueTask<MediaSample?> FindSampleCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.TimeLookupUnavailable);

    /// <summary>Implements supported sync lookup and format-aware dependency/range calculation.</summary>
    /// <param name="presentationTime">The requested time in any timescale.</param>
    /// <param name="cancellationToken">A token checked before dispatch; keep checking it during I/O and scans.</param>
    /// <returns>The safe range, or null if no target or preceding sync sample exists.</returns>
    /// <exception cref="NotSupportedException">The operation is not implemented or a safe range cannot be established.</exception>
    /// <exception cref="ContentFormatException">The profile encounters malformed, unsupported or unrepresentable container data, or a configured limit.</exception>
    /// <exception cref="System.ObjectDisposedException">The owning file or its borrowed source has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading native sample metadata fails.</exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
    protected virtual ValueTask<MediaSeekResult?> SeekCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.SeekUnavailable);
}
