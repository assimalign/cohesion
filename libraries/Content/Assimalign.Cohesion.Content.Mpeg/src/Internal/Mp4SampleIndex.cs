using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal sealed class Mp4SampleIndex : MediaSampleIndex
{
    private readonly SampleDecodingTimeBox _times;
    private readonly SampleCompositionTimeBox? _composition;
    private readonly SampleSizeBox? _sizes;
    private readonly CompactSampleSizeBox? _compact;
    private readonly SampleToChunkBox _chunks;
    private readonly ChunkOffsetBox? _offsets;
    private readonly ChunkOffset64BitBox? _wideOffsets;
    private readonly SyncSampleTableBox? _sync;
    private readonly Mp4EditMap _edits;
    private readonly uint _scale;
    private readonly long _position;
    private readonly bool _local;
    private bool _safeSeek;
    private readonly List<Mp4FragmentRun> _runs = new();

    internal Mp4SampleIndex(ulong trackId, uint scale, SampleTableBox table, Mp4EditMap edits, bool local,
        IList<BmffBox> topLevel, CancellationToken cancellationToken)
    {
        TrackId = trackId;
        _scale = scale;
        _position = table.Offset;
        _edits = edits;
        _local = local;
        _safeSeek = local && !Mp4Tree.HasDependencies(table.Children);
        _times = Mp4Tree.Required<SampleDecodingTimeBox>(table);
        _composition = Mp4Tree.Child<SampleCompositionTimeBox>(table.Children);
        _sizes = Mp4Tree.Child<SampleSizeBox>(table.Children);
        _compact = Mp4Tree.Child<CompactSampleSizeBox>(table.Children);
        _chunks = Mp4Tree.Required<SampleToChunkBox>(table);
        _offsets = Mp4Tree.Child<ChunkOffsetBox>(table.Children);
        _wideOffsets = Mp4Tree.Child<ChunkOffset64BitBox>(table.Children);
        _sync = Mp4Tree.Child<SyncSampleTableBox>(table.Children);
        if ((_sizes is null) == (_compact is null) || (_offsets is null) == (_wideOffsets is null))
        {
            throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, table.Offset);
        }
        ProgressiveCount = _sizes is not null ? _sizes.SampleSize != 0 ? _sizes.SampleCount : _sizes.Entries.LongLength : _compact!.Entries.LongLength;
        TotalCount = ProgressiveCount;
        try { Validate(topLevel, Mp4Tree.Required<SampleDescriptionBox>(table).Children.Count, cancellationToken); }
        catch (OverflowException error)
        {
            throw new ContentFormatException(ErrorMessages.TimestampRange, ContentFormatErrorCode.InvalidValue, table.Offset, error);
        }
    }

    public override ulong TrackId { get; }
    public override long? Count => TotalCount;
    public override bool CanLookupByIndex => _local;
    public override bool CanLookupByTime => _local;
    public override bool CanSeek => _safeSeek;
    internal long ProgressiveCount { get; }
    internal long TotalCount { get; private set; }
    internal long DecodeEnd { get; private set; }
    internal long PresentationEnd { get; private set; }
    internal Mp4EditMap Edits => _edits;
    internal uint Scale => _scale;
    internal IReadOnlyList<Mp4FragmentRun> Runs => _runs;
    internal long ChunkCount => _offsets?.Entries.LongLength ?? _wideOffsets!.Entries.LongLength;
    internal uint InitialDescription => _chunks.Entries.Length == 0 ? 1 : _chunks.Entries[0].SampleDescriptionIndex;

    internal void AddRun(Mp4FragmentRun run, long decodeEnd, long presentationEnd, bool safeSeek)
    {
        _runs.Add(run);
        TotalCount = checked(TotalCount + run.Box.Entries.LongLength);
        DecodeEnd = decodeEnd;
        PresentationEnd = Math.Max(PresentationEnd, presentationEnd);
        _safeSeek &= safeSeek;
    }

    private uint Size(long index) => _sizes is not null ?
        _sizes.SampleSize != 0 ? _sizes.SampleSize : _sizes.Entries[(int)index] : _compact!.Entries[(int)index];

    private long Offset(long chunk)
    {
        var value = _offsets is not null ? _offsets.Entries[(int)chunk] : _wideOffsets!.Entries[(int)chunk];
        if (value > long.MaxValue)
        {
            throw new ContentFormatException(ErrorMessages.SampleRangeInvalid, ContentFormatErrorCode.InvalidStructure, _offsets?.Offset ?? _wideOffsets!.Offset);
        }
        return (long)value;
    }

    private void Validate(IList<BmffBox> boxes, int descriptions, CancellationToken token)
    {
        long count = 0;
        long duration = 0;
        foreach (var entry in _times.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.SampleCount == 0) { throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, _times.Offset); }
            count = checked(count + entry.SampleCount);
            duration = checked(duration + (long)entry.SampleCount * entry.SampleDelta);
        }
        if (count != ProgressiveCount)
        {
            throw new ContentFormatException(ErrorMessages.SampleCountMismatch, ContentFormatErrorCode.InvalidStructure, _times.Offset);
        }
        DecodeEnd = duration;
        if (_composition is not null)
        {
            count = 0;
            foreach (var entry in _composition.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.SampleCount == 0) { throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, _composition.Offset); }
                count = checked(count + entry.SampleCount);
            }
            if (count != ProgressiveCount) { throw new ContentFormatException(ErrorMessages.SampleCountMismatch, ContentFormatErrorCode.InvalidStructure, _composition.Offset); }
        }
        if (_sync is not null)
        {
            foreach (var number in _sync.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (number == 0 || number > ProgressiveCount) { throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, _sync.Offset); }
            }
        }
        // Intersect timing runs without expanding samples. Durations are nonnegative, so
        // the final sample in each intersection has the largest presentation end there.
        var compositionRun = 0;
        long compositionRemaining = _composition is { Entries.Length: > 0 } ? _composition.Entries[0].SampleCount : ProgressiveCount;
        long decode = 0;
        foreach (var timing in _times.Entries)
        {
            long remaining = timing.SampleCount;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var take = Math.Min(remaining, compositionRemaining);
                var displacement = _composition is null ? 0 : _composition.Entries[compositionRun].SampleOffset;
                decode = checked(decode + take * timing.SampleDelta);
                PresentationEnd = Math.Max(PresentationEnd, checked(decode + displacement));
                remaining -= take;
                compositionRemaining -= take;
                if (compositionRemaining == 0 && _composition is not null && ++compositionRun < _composition.Entries.Length)
                {
                    compositionRemaining = _composition.Entries[compositionRun].SampleCount;
                }
            }
        }
        if (ChunkCount == 0)
        {
            if (ProgressiveCount != 0 || _chunks.Entries.Length != 0)
            {
                throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, _chunks.Offset);
            }
            return;
        }
        if (_chunks.Entries.Length == 0 || _chunks.Entries[0].FirstChunk != 1)
        {
            throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, _chunks.Offset);
        }
        count = 0;
        for (var run = 0; run < _chunks.Entries.Length; run++)
        {
            token.ThrowIfCancellationRequested();
            var entry = _chunks.Entries[run];
            var end = run + 1 < _chunks.Entries.Length ? _chunks.Entries[run + 1].FirstChunk : ChunkCount + 1;
            if (entry.FirstChunk > ChunkCount || end <= entry.FirstChunk || entry.SampleDescriptionIndex == 0 || entry.SampleDescriptionIndex > descriptions)
            {
                throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, _chunks.Offset);
            }
            _safeSeek &= entry.SampleDescriptionIndex == InitialDescription;
            count = checked(count + (end - entry.FirstChunk) * entry.SamplesPerChunk);
        }
        if (count != ProgressiveCount)
        {
            throw new ContentFormatException(ErrorMessages.SampleCountMismatch, ContentFormatErrorCode.InvalidStructure, _chunks.Offset);
        }
        long sample = 0;
        var mapping = 0;
        for (long chunk = 0; chunk < ChunkCount; chunk++)
        {
            token.ThrowIfCancellationRequested();
            while (mapping + 1 < _chunks.Entries.Length && _chunks.Entries[mapping + 1].FirstChunk <= chunk + 1) { mapping++; }
            var samples = _chunks.Entries[mapping].SamplesPerChunk;
            long length = 0;
            if (_sizes is { SampleSize: > 0 }) { length = checked((long)samples * _sizes.SampleSize); sample += samples; }
            else
            {
                for (var i = 0u; i < samples; i++)
                {
                    token.ThrowIfCancellationRequested();
                    length = checked(length + Size(sample++));
                }
            }
            if (_local && !Mp4Tree.InMedia(boxes, Offset(chunk), length))
            {
                throw new ContentFormatException(ErrorMessages.SampleRangeInvalid, ContentFormatErrorCode.InvalidStructure, _offsets?.Offset ?? _wideOffsets!.Offset);
            }
        }
    }

    private MediaSample Describe(long index, long dts, long composition, long offset, long size, bool sync)
    {
        try
        {
            var decode = new MediaTimestamp(dts, _scale);
            var pts = new MediaTimestamp(checked(dts + composition), _scale) + _edits.Shift;
            return new MediaSample(TrackId, index, decode, pts, offset, size, sync);
        }
        catch (OverflowException error)
        {
            throw new ContentFormatException(ErrorMessages.TimestampRange, ContentFormatErrorCode.InvalidValue, _position, error);
        }
    }

    protected override ValueTask<MediaSample?> GetSampleCoreAsync(long index, CancellationToken cancellationToken = default)
    {
        if (index >= TotalCount) { return ValueTask.FromResult<MediaSample?>(null); }
        if (index < ProgressiveCount)
        {
            long remaining = index;
            long dts = 0;
            foreach (var run in _times.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var taken = Math.Min(remaining, run.SampleCount);
                dts += taken * run.SampleDelta;
                if (remaining < run.SampleCount) { break; }
                remaining -= run.SampleCount;
            }
            long composition = 0;
            if (_composition is not null)
            {
                remaining = index;
                foreach (var run in _composition.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (remaining < run.SampleCount) { composition = run.SampleOffset; break; }
                    remaining -= run.SampleCount;
                }
            }
            remaining = index;
            long chunk = 0;
            long first = 0;
            for (var runIndex = 0; runIndex < _chunks.Entries.Length; runIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var run = _chunks.Entries[runIndex];
                var end = runIndex + 1 < _chunks.Entries.Length ? _chunks.Entries[runIndex + 1].FirstChunk : ChunkCount + 1;
                var span = (end - run.FirstChunk) * run.SamplesPerChunk;
                if (remaining < span)
                {
                    chunk = run.FirstChunk - 1 + remaining / run.SamplesPerChunk;
                    first = index - remaining % run.SamplesPerChunk;
                    break;
                }
                remaining -= span;
            }
            var offset = Offset(chunk);
            if (_sizes is { SampleSize: > 0 }) { offset += (index - first) * _sizes.SampleSize; }
            else
            {
                for (var i = first; i < index; i++) { cancellationToken.ThrowIfCancellationRequested(); offset += Size(i); }
            }
            var sync = _sync is null || Array.BinarySearch(_sync.Entries, (uint)(index + 1)) >= 0;
            return ValueTask.FromResult<MediaSample?>(Describe(index, dts, composition, offset, Size(index), sync));
        }
        foreach (var run in _runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < run.Index || index - run.Index >= run.Box.Entries.LongLength) { continue; }
            var dts = run.DecodeTime;
            var offset = run.Offset;
            var ordinal = (int)(index - run.Index);
            for (var i = 0; i < ordinal; i++) { cancellationToken.ThrowIfCancellationRequested(); dts += run.SampleDuration(i); offset += run.SampleSize(i); }
            return ValueTask.FromResult<MediaSample?>(Describe(index, dts, run.CompositionOffset(ordinal), offset, run.SampleSize(ordinal), run.IsSync(ordinal)));
        }
        return ValueTask.FromResult<MediaSample?>(null);
    }

    protected override ValueTask<MediaSample?> FindSampleCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        MediaSample? best = null;
        var cursor = new Cursor(this);
        while (cursor.MoveNext(out var sample))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_edits.Presents(sample.PresentationTime) && sample.PresentationTime <= presentationTime &&
                (!best.HasValue || sample.PresentationTime > best.Value.PresentationTime))
            {
                best = sample;
            }
        }
        return ValueTask.FromResult(best);
    }

    protected override ValueTask<MediaSeekResult?> SeekCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        var target = FindSampleCoreAsync(presentationTime, cancellationToken).Result;
        if (!target.HasValue) { return ValueTask.FromResult<MediaSeekResult?>(null); }
        MediaSample? sync = null;
        var cursor = new Cursor(this);
        while (cursor.MoveNext(out var sample) && sample.Index <= target.Value.Index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample.IsSync && sample.PresentationTime <= presentationTime &&
                (!sync.HasValue || sample.PresentationTime > sync.Value.PresentationTime)) { sync = sample; }
        }
        if (!sync.HasValue) { return ValueTask.FromResult<MediaSeekResult?>(null); }
        long start = long.MaxValue;
        long end = 0;
        cursor = new Cursor(this);
        while (cursor.MoveNext(out var sample) && sample.Index <= target.Value.Index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sample.Index >= sync.Value.Index)
            {
                start = Math.Min(start, sample.ByteOffset);
                end = Math.Max(end, sample.ByteOffset + sample.ByteLength);
            }
        }
        foreach (var run in _runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.Box.Entries.Length != 0 && run.Index <= target.Value.Index && run.Index + run.Box.Entries.Length > sync.Value.Index)
            {
                start = Math.Min(start, run.MoofOffset);
                end = Math.Max(end, run.MoofEnd);
            }
        }
        return ValueTask.FromResult<MediaSeekResult?>(new MediaSeekResult(sync.Value, target.Value, start, end - start));
    }

    protected override async IAsyncEnumerable<MediaSample> ReadSamplesCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_local) { throw new NotSupportedException(ErrorMessages.ExternalDataUnsupported); }
        var cursor = new Cursor(this);
        while (cursor.MoveNext(out var sample))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return sample;
        }
        await ValueTask.CompletedTask;
    }

    // This value cursor holds one native run position per table, never one object per sample.
    private struct Cursor
    {
        private readonly Mp4SampleIndex _owner;
        private long _index;
        private long _dts;
        private long _offset;
        private int _timeRun;
        private uint _timeRemaining;
        private int _compositionRun;
        private uint _compositionRemaining;
        private int _chunk;
        private int _mapping;
        private uint _chunkRemaining;
        private int _sync;
        private int _fragment;
        private int _fragmentSample;
        internal Cursor(Mp4SampleIndex owner)
        {
            this = default;
            _owner = owner;
        }

        internal bool MoveNext(out MediaSample sample)
        {
            sample = default;
            if (_index >= _owner.TotalCount) { return false; }
            if (_index < _owner.ProgressiveCount)
            {
                if (_timeRemaining == 0) { _timeRemaining = _owner._times.Entries[_timeRun].SampleCount; }
                var duration = _owner._times.Entries[_timeRun].SampleDelta;
                long composition = 0;
                if (_owner._composition is not null)
                {
                    if (_compositionRemaining == 0) { _compositionRemaining = _owner._composition.Entries[_compositionRun].SampleCount; }
                    composition = _owner._composition.Entries[_compositionRun].SampleOffset;
                    if (--_compositionRemaining == 0) { _compositionRun++; }
                }
                if (_chunkRemaining == 0)
                {
                    if (_index != 0) { _chunk++; }
                    while (_mapping + 1 < _owner._chunks.Entries.Length && _owner._chunks.Entries[_mapping + 1].FirstChunk <= _chunk + 1) { _mapping++; }
                    _chunkRemaining = _owner._chunks.Entries[_mapping].SamplesPerChunk;
                    _offset = _owner.Offset(_chunk);
                }
                var sync = _owner._sync is null;
                if (_owner._sync is not null && _sync < _owner._sync.Entries.Length && _owner._sync.Entries[_sync] == _index + 1)
                {
                    sync = true;
                    _sync++;
                }
                var size = _owner.Size(_index);
                sample = _owner.Describe(_index, _dts, composition, _offset, size, sync);
                _offset += size;
                _dts += duration;
                _chunkRemaining--;
                if (--_timeRemaining == 0) { _timeRun++; }
            }
            else
            {
                while (_fragment < _owner._runs.Count && _fragmentSample == _owner._runs[_fragment].Box.Entries.Length)
                {
                    _fragment++;
                    _fragmentSample = 0;
                }
                var run = _owner._runs[_fragment];
                if (_fragmentSample == 0) { _offset = run.Offset; _dts = run.DecodeTime; }
                var size = run.SampleSize(_fragmentSample);
                sample = _owner.Describe(_index, _dts, run.CompositionOffset(_fragmentSample), _offset, size, run.IsSync(_fragmentSample));
                _offset += size;
                _dts += run.SampleDuration(_fragmentSample);
                _fragmentSample++;
            }
            _index++;
            return true;
        }
    }
}
