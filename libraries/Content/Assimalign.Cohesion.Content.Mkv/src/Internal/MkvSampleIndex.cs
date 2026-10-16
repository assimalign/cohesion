using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal sealed class MkvSampleIndex : MediaSampleIndex
{
    private readonly MkvDocument _document;
    private readonly MkvTrack _track;
    private List<ClusterSummary>? _summaries;

    internal MkvSampleIndex(MkvDocument document, MkvTrack track) { _document = document; _track = track; }
    public override ulong TrackId => _track.Id;
    public override long? Count => null;
    public override bool CanLookupByIndex => true;
    public override bool CanLookupByTime => true;
    public override bool CanSeek => _track.SupportsSeek;
    internal long CueLookups { get; private set; }

    protected override async IAsyncEnumerable<MediaSample> ReadSamplesCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ValueTask.CompletedTask;
        foreach (var item in Walk(cancellationToken)) { yield return item.Sample; }
    }
    protected override ValueTask<MediaSample?> GetSampleCoreAsync(long index, CancellationToken cancellationToken = default)
    {
        foreach (var item in Walk(cancellationToken)) { if (item.Sample.Index == index) { return ValueTask.FromResult<MediaSample?>(item.Sample); } }
        return ValueTask.FromResult<MediaSample?>(null);
    }
    protected override ValueTask<MediaSample?> FindSampleCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        MediaSample? best = null;
        foreach (var item in Walk(cancellationToken))
        { if (Eligible(item.Sample, presentationTime, best)) { best = item.Sample; } }
        return ValueTask.FromResult(best);
    }
    protected override ValueTask<MediaSeekResult?> SeekCoreAsync(MediaTimestamp presentationTime, CancellationToken cancellationToken = default)
    {
        _document.EnsureOpen();
        MkvCue? cue = null;
        var hasCues = false;
        foreach (var candidate in _document.Cues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.TrackId != TrackId) { continue; }
            hasCues = true;
            if (candidate.HasCodecState) { throw new NotSupportedException(ErrorMessages.CodecStateUnsupported); }
            var time = _document.BlockTimestamp(candidate.Timestamp, 0, _track.CodecDelay, candidate.Cluster.Offset);
            if (time <= presentationTime && (cue is null || candidate.Timestamp > cue.Timestamp)) { cue = candidate; }
        }
        var summaries = hasCues ? GetSummaries(cancellationToken) : null;
        SampleLocation? target = null;
        SampleLocation? sync = null;
        if (cue is not null)
        {
            CueLookups++;
            foreach (var summary in summaries!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(summary.Cluster, cue.Cluster)) { continue; }
                foreach (var item in Walk(cancellationToken, summary.Cluster, summary.FirstIndex))
                {
                    if (Eligible(item.Sample, presentationTime, target?.Sample)) { target = item; }
                    if (item.Sample.IsSync && Eligible(item.Sample, presentationTime, sync?.Sample)) { sync = item; }
                }
                break;
            }
        }
        if (summaries is null)
        {
            foreach (var item in Walk(cancellationToken))
            { if (Eligible(item.Sample, presentationTime, target?.Sample)) { target = item; } }
        }
        else
        {
            // Timestamp bounds retain correctness with sparse cues and arbitrary PTS reordering,
            // while source ordinals need only one initial framing scan per occupied cluster.
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceEquals(summary.Cluster, cue?.Cluster) || summary.Minimum > presentationTime ||
                    target is not null && summary.Maximum < target.Sample.PresentationTime) { continue; }
                foreach (var item in Walk(cancellationToken, summary.Cluster, summary.FirstIndex))
                { if (Eligible(item.Sample, presentationTime, target?.Sample)) { target = item; } }
            }
        }
        if (target is null) { return ValueTask.FromResult<MediaSeekResult?>(null); }
        if (sync?.Sample.Index > target.Sample.Index || sync?.Sample.PresentationTime > target.Sample.PresentationTime) { sync = null; }
        if (summaries is null)
        {
            foreach (var item in Walk(cancellationToken))
            {
                if (item.Sample.Index <= target.Sample.Index && item.Sample.IsSync &&
                    Eligible(item.Sample, target.Sample.PresentationTime, sync?.Sample)) { sync = item; }
            }
        }
        else
        {
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (summary.FirstIndex > target.Sample.Index || summary.MinimumSync is null ||
                    summary.MinimumSync > target.Sample.PresentationTime ||
                    sync is not null && summary.MaximumSync < sync.Sample.PresentationTime) { continue; }
                foreach (var item in Walk(cancellationToken, summary.Cluster, summary.FirstIndex))
                {
                    if (item.Sample.Index <= target.Sample.Index && item.Sample.IsSync &&
                        Eligible(item.Sample, target.Sample.PresentationTime, sync?.Sample)) { sync = item; }
                }
            }
        }
        if (sync is null) { return ValueTask.FromResult<MediaSeekResult?>(null); }
        var start = _track.SeekPreRoll == 0 ? sync.Cluster.Offset : _document.Clusters[0].Offset;
        var end = _document.ClusterEnd(target.Cluster);
        var inspectReferences = summaries is null;
        if (summaries is not null)
        {
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (summary.HasCodecState) { throw new NotSupportedException(ErrorMessages.CodecStateUnsupported); }
                inspectReferences |= summary.HasReferences;
            }
        }
        if (inspectReferences)
        {
            ValidateReferences(ref start, ref end, cancellationToken);
        }
        return ValueTask.FromResult<MediaSeekResult?>(new MediaSeekResult(sync.Sample, target.Sample, start, end - start));
    }

    private void ValidateReferences(ref long start, ref long end, CancellationToken cancellationToken)
    {
        foreach (var item in Walk(cancellationToken))
        {
            if (item.HasCodecState) { throw new NotSupportedException(ErrorMessages.CodecStateUnsupported); }
            if (item.HasReferences)
            {
                // ReferenceBlock may cross the requested source-order interval. Resolve its native
                // timestamp before claiming a safe range; no codec dependency is guessed.
                foreach (var reference in MkvElements.All(item.Group, 0xFB))
                {
                    var wanted = MkvDocument.Nanoseconds((Int128)item.BlockTime + (Int128)(long)reference.Value! * _document.TimestampScale, reference.Offset);
                    SampleLocation? dependency = null;
                    foreach (var other in Walk(cancellationToken))
                    { if (other.Sample.PresentationTime == wanted) { dependency = other; break; } }
                    if (dependency is null) { throw new NotSupportedException(ErrorMessages.ReferenceUnsupported); }
                    // Conservative complete media coverage includes transitive and forward
                    // references. Every reference in this track is checked by the outer walk.
                    start = _document.Clusters[0].Offset;
                    end = _document.ClusterEnd(_document.Clusters[^1]);
                }
            }
        }
    }

    private List<ClusterSummary> GetSummaries(CancellationToken cancellationToken)
    {
        if (_summaries is not null) { return _summaries; }
        var summaries = new List<ClusterSummary>();
        ClusterSummary? current = null;
        foreach (var item in Walk(cancellationToken))
        {
            if (!ReferenceEquals(current?.Cluster, item.Cluster))
            {
                current = new ClusterSummary(item.Cluster, item.Sample);
                summaries.Add(current);
            }
            current!.Include(item);
        }
        // Publish only a complete snapshot. Cancellation leaves no partial source ordinals.
        _summaries = summaries;
        return summaries;
    }

    private static bool Eligible(MediaSample sample, MediaTimestamp time, MediaSample? current) => sample.PresentationTime <= time &&
        (current is null || sample.PresentationTime > current.Value.PresentationTime ||
        sample.PresentationTime == current.Value.PresentationTime && sample.Index < current.Value.Index);

    private IEnumerable<SampleLocation> Walk(CancellationToken cancellationToken, EbmlElement? onlyCluster = null, long firstIndex = 0)
    {
        _document.EnsureOpen();
        var index = firstIndex;
        var clusterCount = onlyCluster is null ? _document.Clusters.Count : 1;
        for (var clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cluster = onlyCluster ?? _document.Clusters[clusterIndex];
            var clusterTime = MkvElements.Number(cluster, 0xE7);
            foreach (var child in cluster.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Identifier.EncodedValue == 0xAF)
                { throw new ContentFormatException(ErrorMessages.EncryptedBlockUnsupported, ContentFormatErrorCode.Unsupported, child.Offset); }
                var simple = child.Identifier.EncodedValue == 0xA3;
                var group = child.Identifier.EncodedValue == 0xA0 ? child : null;
                var element = simple ? child : MkvElements.Find(group, 0xA1);
                if (element is null) { continue; }
                var payloadOffset = element.Offset + element.Identifier.Length + element.SizeLength;
                var block = MkvBlock.Parse((BinarySegment)element.Value!, payloadOffset, simple, _document.Options.MaxLaceFrames);
                var track = _document.Track(block.TrackNumber);
                if (track is null)
                { throw new ContentFormatException(ErrorMessages.BlockTrackMissing, ContentFormatErrorCode.InvalidStructure, payloadOffset); }
                if (!track.AllowsLacing && block.Frames.Count > 1)
                { throw new ContentFormatException(ErrorMessages.LacingDisabled, ContentFormatErrorCode.InvalidStructure, payloadOffset); }
                if (track.Id != TrackId) { continue; }
                var blockTime = _document.BlockTimestamp(clusterTime, block.RelativeTimestamp, track.CodecDelay, payloadOffset).Value;
                for (var frame = 0; frame < block.Frames.Count; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (frame > 0 && track.DefaultDuration == 0) { throw new NotSupportedException(ErrorMessages.LaceTimeUnknown); }
                    var time = MkvDocument.Nanoseconds((Int128)blockTime + (Int128)frame * track.DefaultDuration, payloadOffset);
                    var sync = simple ? block.IsKeyframe : MkvElements.Find(group, 0xFB) is null;
                    yield return new SampleLocation(new MediaSample(TrackId, index++, null, time, block.FrameOffsets[frame], block.Frames[frame].Length, sync),
                        cluster, group, blockTime);
                }
            }
        }
    }

    private sealed class ClusterSummary
    {
        internal ClusterSummary(EbmlElement cluster, MediaSample first)
        { Cluster = cluster; FirstIndex = first.Index; Minimum = first.PresentationTime; Maximum = first.PresentationTime; }
        internal EbmlElement Cluster { get; }
        internal long FirstIndex { get; }
        internal MediaTimestamp Minimum { get; private set; }
        internal MediaTimestamp Maximum { get; private set; }
        internal MediaTimestamp? MinimumSync { get; private set; }
        internal MediaTimestamp? MaximumSync { get; private set; }
        internal bool HasReferences { get; private set; }
        internal bool HasCodecState { get; private set; }
        internal void Include(SampleLocation item)
        {
            var time = item.Sample.PresentationTime;
            if (time < Minimum) { Minimum = time; }
            if (time > Maximum) { Maximum = time; }
            if (item.Sample.IsSync)
            {
                if (MinimumSync is null || time < MinimumSync) { MinimumSync = time; }
                if (MaximumSync is null || time > MaximumSync) { MaximumSync = time; }
            }
            HasReferences |= item.HasReferences;
            HasCodecState |= item.HasCodecState;
        }
    }

    private sealed record SampleLocation(MediaSample Sample, EbmlElement Cluster, EbmlElement? Group, long BlockTime)
    {
        internal bool HasReferences => MkvElements.Find(Group, 0xFB) is not null;
        internal bool HasCodecState => MkvElements.Find(Group, 0xA4) is not null;
    }
}
