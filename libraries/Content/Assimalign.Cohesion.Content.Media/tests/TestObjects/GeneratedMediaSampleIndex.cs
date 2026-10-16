using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Media.Tests;

// All data is generated from formulas: no file, codec data, or per-sample table is allocated.
// Decode order in each group is I/P/B/B; presentation ordinal is [0, 3, 1, 2].
// Sync points repeat at 0, 8, 20, 44 within 64-sample runs (gaps 8, 12, 24, 20).
// Payloads are separated by 64 KiB to model interleaving and offsets beyond 40 GB.
internal sealed class GeneratedMediaSampleIndex : MediaSampleIndex
{
    private readonly long _count;
    private readonly MediaTimestamp _shift;
    private readonly bool _hasSync;

    internal GeneratedMediaSampleIndex(long count, MediaTimestamp shift = default, bool hasSync = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNotEqual(count % 4, 0, nameof(count));
        _count = count;
        _shift = shift;
        _hasSync = hasSync;
    }

    public override ulong TrackId => 1;
    public override long? Count => _count;
    public override bool CanLookupByIndex => true;
    public override bool CanLookupByTime => true;
    public override bool CanSeek => true;
    internal long SamplesProduced { get; private set; }
    internal int EnumerationsStarted { get; private set; }
    internal int EnumerationsDisposed { get; private set; }

    protected override async IAsyncEnumerable<MediaSample> ReadSamplesCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnumerationsStarted++;
        try
        {
            await ValueTask.CompletedTask;
            for (long i = 0; i < _count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateSample(i);
            }
        }
        finally
        {
            EnumerationsDisposed++;
        }
    }

    protected override ValueTask<MediaSample?> GetSampleCoreAsync(long index, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MediaSample?>(index < _count ? CreateSample(index) : null);

    protected override ValueTask<MediaSample?> FindSampleCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        if (_count == 0 || presentationTime < _shift)
        {
            return ValueTask.FromResult<MediaSample?>(null);
        }
        var lastTime = new MediaTimestamp((_count - 1) * 3003, 90000) + _shift;
        var ordinal = presentationTime >= lastTime ? _count - 1 :
            (presentationTime - _shift).Rescale(90000, MidpointRounding.ToNegativeInfinity).Value / 3003;
        var index = ordinal / 4 * 4 + ((ordinal % 4) switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 });
        return ValueTask.FromResult<MediaSample?>(CreateSample(index));
    }

    protected override ValueTask<MediaSeekResult?> SeekCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        var target = FindSampleCoreAsync(presentationTime, cancellationToken).Result;
        if (target is not { } sample || !_hasSync)
        {
            return ValueTask.FromResult<MediaSeekResult?>(null);
        }
        var run = sample.Index / 64;
        var local = sample.Index % 64;
        var syncIndex = run * 64 + (local >= 44 ? 44 : local >= 20 ? 20 : local >= 8 ? 8 : 0);
        var sync = CreateSample(syncIndex);
        var start = sync.ByteOffset - 32; // Include synthetic framing; initialization is already known.
        var end = sample.ByteOffset + sample.ByteLength;
        return ValueTask.FromResult<MediaSeekResult?>(new MediaSeekResult(sync, sample, start, end - start));
    }

    private MediaSample CreateSample(long index)
    {
        SamplesProduced++;
        var ordinal = index / 4 * 4 + ((index % 4) switch { 0 => 0, 1 => 3, 2 => 1, _ => 2 });
        var decode = new MediaTimestamp(index * 3003, 90000);
        var presentation = new MediaTimestamp(ordinal * 3003, 90000) + _shift;
        var local = index % 64;
        return new MediaSample(TrackId, index, decode, presentation, 4096 + index * 65536,
            700 + index % 13 * 17, _hasSync && local is 0 or 8 or 20 or 44);
    }
}
