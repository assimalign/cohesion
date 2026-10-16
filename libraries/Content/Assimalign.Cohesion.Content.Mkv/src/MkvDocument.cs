using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;
using Assimalign.Cohesion.Content.Mkv.Internal;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>An editable Matroska/WebM metadata document and a lazy media view of its source snapshot.</summary>
/// <remarks>Keep the seekable source open and stable. Operations sharing its cursor are sequential.
/// Save to separate storage, then load that storage for relocated sample coordinates. OpenRead always
/// returns the original source snapshot; edits affect Save. Use EbmlReader and MkvSchema.Registry for
/// non-seekable streaming with MkvBlockReader, and EbmlWriter with MkvSchema.Matroska for forward-only emission.</remarks>
public sealed class MkvDocument : IMediaFile
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly long _start;
    private readonly long _length;
    private readonly long _origin;
    private readonly MkvLayout _layout;
    private readonly Dictionary<EbmlElement, EbmlElement> _positionTargets = new();
    private readonly Dictionary<ulong, MkvTrack> _trackMap = new();
    private readonly List<EbmlElement> _clusters = new();
    private readonly List<MkvCue> _cues = new();
    private readonly List<ContentFormatException> _diagnostics = new();
    private bool _disposed;
    private static readonly ContentFormat _matroskaFormat = new()
    { Name = "Matroska", Kind = ContentKind.Media, MediaTypes = ["video/matroska", "audio/matroska"], FileExtensions = [".mkv", ".mka"], Specification = "RFC 9559" };
    private static readonly ContentFormat _webmFormat = new()
    { Name = "WebM", Kind = ContentKind.Media, MediaTypes = ["video/webm", "audio/webm"], FileExtensions = [".webm"], Specification = "https://www.webmproject.org/docs/container/" };

    private MkvDocument(Stream source, EbmlDocument document, MkvReaderOptions options, bool leaveOpen, long start, long length)
    {
        _source = source; _leaveOpen = leaveOpen; _start = start; _length = length;
        _origin = source is ContentSliceStream slice ? slice.SourceOffset : 0;
        Ebml = document; Options = options;
        Segment = MkvElements.FindBody(document, 0x18538067) ??
            throw new ContentFormatException(ErrorMessages.SegmentRequired, ContentFormatErrorCode.InvalidStructure, start + _origin);
        Chapters = new MkvChildList<MkvChapterEdition>(GetChapters, 0x45B9,
            element => new MkvChapterEdition(element), edition => edition.Element,
            () => { if (GetChapters(false) is { } chapters) { Segment.Children.Remove(chapters); } });
        _layout = new MkvLayout(document.Schema, options.Ebml.MaxDepth);
        _layout.Capture(document.Header.Element, source, _origin);
        foreach (var root in document.Body.Elements) { _layout.Capture(root, source, _origin); }
        SegmentDataOffset = Segment.Offset + Segment.Identifier.Length + Segment.SizeLength;
        Initialize();
    }

    /// <summary>Gets the editable native tree. Edit metadata here; cluster mutation is outside this document's save contract.</summary>
    public EbmlDocument Ebml { get; }
    /// <summary>Gets the Segment's ordered metadata and lazy cluster tree.</summary>
    public EbmlElement Segment { get; }
    /// <summary>Gets the editable chapter editions in wire order, empty when Chapters is absent.</summary>
    /// <remarks>Adding the first edition creates Chapters. Removing the last edition removes that
    /// whole container. Collections are live views over Ebml; reading does not materialize defaults.</remarks>
    public IList<MkvChapterEdition> Chapters { get; }
    /// <summary>Gets profile diagnostics, including ignored deprecated TrackTimestampScale fields.</summary>
    public IReadOnlyList<ContentFormatException> Diagnostics => _diagnostics.AsReadOnly();
    /// <inheritdoc/>
    public IReadOnlyList<IMediaTrack> Tracks { get; private set; } = Array.Empty<IMediaTrack>();
    /// <inheritdoc/>
    public MediaTimestamp? Duration { get; private set; }
    /// <inheritdoc/>
    public bool IsLive { get; private set; }
    /// <inheritdoc/>
    public string? Name => MkvElements.Text(MkvElements.Find(Segment, 0x1549A966), 0x7BA9);
    /// <inheritdoc/>
    public ContentFormat Format => Ebml.Header.DocType == "webm" ? _webmFormat : _matroskaFormat;
    /// <inheritdoc/>
    public string? MediaType => Ebml.Header.DocType == "webm" ? "video/webm" : "video/matroska";
    /// <inheritdoc/>
    public long? Length => _length;
    /// <inheritdoc/>
    public bool IsReadOnly => true;
    /// <inheritdoc/>
    public bool CanReopen => true;

    internal MkvReaderOptions Options { get; }
    internal long SegmentDataOffset { get; }
    internal ulong TimestampScale { get; private set; }
    internal IReadOnlyList<EbmlElement> Clusters => _clusters;
    internal IReadOnlyList<MkvCue> Cues => _cues;

    /// <summary>Loads a bounded editable tree with lazy media and attachment bytes.</summary>
    /// <param name="source">A readable seekable source, at the start of one document.</param>
    /// <param name="options">Limits, copied at creation.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open. Ownership transfers only on success.</param>
    /// <returns>The document and media snapshot.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source cannot read/seek or options request leniency.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is invalid.</exception>
    /// <exception cref="ContentFormatException">The source violates framing, schema, profile or configured limits.</exception>
    public static MkvDocument Load(Stream source, MkvReaderOptions? options = null, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek) { throw new ArgumentException(ErrorMessages.SourceSeekable, nameof(source)); }
        var effective = (options ?? new MkvReaderOptions()).Snapshot();
        var start = source.Position;
        // Check profile counts before EBML's tree allocates the corresponding nodes.
        Preflight(source, effective);
        source.Position = start;
        var native = EbmlDocument.Load(source, MkvSchema.Matroska, effective.Ebml, registry: MkvSchema.Registry);
        if (native.Header.DocType is not ("matroska" or "webm") || native.Header.EBMLMaxIDLength != 4 || native.Header.DocTypeReadVersion > 4)
        { throw new ContentFormatException(ErrorMessages.ProfileUnsupported, ContentFormatErrorCode.Unsupported, native.Header.Element.Offset); }
        return new MkvDocument(source, native, effective, leaveOpen, start, source.Length - start);
    }

    /// <summary>Saves metadata edits, recomputes SeekHead/Cues positions, and copies unchanged clusters verbatim.</summary>
    /// <param name="destination">Separate writable storage, borrowed by the save operation.</param>
    /// <exception cref="ArgumentNullException">The destination is null.</exception>
    /// <exception cref="ArgumentException">The destination is not writable or is the source.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">The edited schema is invalid or a source was truncated.</exception>
    /// <exception cref="NotSupportedException">Clusters or referenced position targets were removed/edited.</exception>
    public void Save(Stream destination)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(destination, _source) || !destination.CanWrite) { throw new ArgumentException(ErrorMessages.SaveDestination, nameof(destination)); }
        Ebml.Schema.Validate(Ebml, maxDepth: Options.Ebml.MaxDepth);
        ValidateMetadata();
        ValidateWebM();
        var clusterIndex = 0;
        foreach (var element in MkvElements.All(Segment, 0x1F43B675))
        {
            if (clusterIndex >= _clusters.Count || !ReferenceEquals(element, _clusters[clusterIndex++]))
            { throw new NotSupportedException(ErrorMessages.ClusterEditUnsupported); }
        }
        foreach (var cluster in _clusters)
        {
            if (!_layout.Unchanged(cluster) || !Segment.Children.Contains(cluster)) { throw new NotSupportedException(ErrorMessages.ClusterEditUnsupported); }
        }
        MaintainPositions();
        Ebml.Schema.Validate(Ebml, maxDepth: Options.Ebml.MaxDepth);
        _layout.Write(destination, Ebml.Header.Element);
        foreach (var root in Ebml.Body.Elements) { _layout.Write(destination, root); }
    }

    /// <summary>Materializes one attachment only within the configured explicit buffering cap.</summary>
    /// <param name="attachedFile">An AttachedFile master in this document.</param>
    /// <returns>A copy of FileData; lazy access is available directly through its native BinarySegment value.</returns>
    /// <exception cref="ArgumentNullException">The attachment is null.</exception>
    /// <exception cref="ArgumentException">The element is not an attachment with FileData.</exception>
    /// <exception cref="ContentFormatException">The data exceeds the cap or is truncated.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public byte[] ReadAttachmentData(EbmlElement attachedFile)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(attachedFile);
        if (attachedFile.Identifier.EncodedValue != 0x61A7 || MkvElements.Find(attachedFile, 0x465C)?.Value is not BinarySegment data)
        { throw new ArgumentException(ErrorMessages.AttachmentInvalid, nameof(attachedFile)); }
        if (data.Length > Options.MaxAttachmentBufferLength)
        { throw new ContentFormatException(ErrorMessages.AttachmentLimit, ContentFormatErrorCode.LimitExceeded, MkvElements.Find(attachedFile, 0x465C)!.Offset); }
        return data.ToArray(Options.MaxAttachmentBufferLength);
    }

    /// <inheritdoc/>
    public Stream OpenRead() { EnsureOpen(); return ContentSliceStream.Create(_source, _start, _length); }
    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenRead()); }
    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; Ebml.Dispose();
        if (!_leaveOpen) { _source.Dispose(); }
    }
    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) { return ValueTask.CompletedTask; }
        _disposed = true; Ebml.Dispose();
        return _leaveOpen ? ValueTask.CompletedTask : _source.DisposeAsync();
    }

    internal void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
    internal long ClusterEnd(EbmlElement cluster) => cluster.Offset + _layout.OriginalLength(cluster);
    internal MkvTrack? Track(ulong id) => _trackMap.GetValueOrDefault(id);
    internal static MediaTimestamp Nanoseconds(Int128 value, long position)
    {
        if (value < long.MinValue || value > long.MaxValue)
        { throw new ContentFormatException(ErrorMessages.TimestampInvalid, ContentFormatErrorCode.InvalidValue, position); }
        return new MediaTimestamp((long)value, 1_000_000_000);
    }

    internal MediaTimestamp BlockTimestamp(ulong cluster, short relative, ulong delay, long position)
    {
        try { return Nanoseconds(checked(((Int128)cluster + relative) * TimestampScale - delay), position); }
        catch (OverflowException) { throw new ContentFormatException(ErrorMessages.TimestampInvalid, ContentFormatErrorCode.InvalidValue, position); }
    }

    private void Initialize()
    {
        var info = MkvElements.Find(Segment, 0x1549A966);
        if (MkvElements.Find(info, 0x73A4) is { Value: BinarySegment uuid } uuidElement)
        {
            var bytes = uuid.ToArray(16);
            var nonzero = false;
            foreach (var value in bytes) { nonzero |= value != 0; }
            if (!nonzero) { throw new ContentFormatException(ErrorMessages.SegmentUuidInvalid, ContentFormatErrorCode.InvalidValue, uuidElement.Offset); }
        }
        TimestampScale = MkvElements.Number(info, 0x2AD7B1, 1_000_000);
        if (MkvElements.Find(info, 0x4489) is { Value: double duration } durationElement)
        {
            var nanos = Math.Round(duration * TimestampScale, MidpointRounding.ToEven);
            if (!double.IsFinite(nanos) || nanos < 0 || nanos >= 9223372036854775808.0)
            { throw new ContentFormatException(ErrorMessages.TimestampInvalid, ContentFormatErrorCode.InvalidValue, durationElement.Offset); }
            Duration = new MediaTimestamp((long)nanos, 1_000_000_000);
        }
        var tracks = new List<IMediaTrack>();
        var uids = new HashSet<ulong>();
        foreach (var entry in MkvElements.All(MkvElements.Find(Segment, 0x1654AE6B), 0xAE))
        {
            var track = MkvTrack.Create(this, entry);
            if (!_trackMap.TryAdd(track.Id, track) || !uids.Add(MkvElements.Number(entry, 0x73C5)))
            { throw new ContentFormatException(ErrorMessages.TrackDuplicate, ContentFormatErrorCode.InvalidStructure, entry.Offset); }
            tracks.Add(track);
            if (MkvElements.Find(entry, 0x23314F) is { } scale)
            { _diagnostics.Add(new ContentFormatException(ErrorMessages.TrackScaleIgnored, ContentFormatErrorCode.Unsupported, scale.Offset)); }
        }
        Tracks = tracks.AsReadOnly();
        var targets = new Dictionary<long, EbmlElement>();
        foreach (var element in Segment.Children)
        {
            targets.Add(element.Offset, element);
            if (element.Identifier.EncodedValue == 0x1F43B675) { _clusters.Add(element); }
        }
        foreach (var point in MkvElements.All(MkvElements.Find(Segment, 0x1C53BB6B), 0xBB))
        {
            foreach (var position in MkvElements.All(point, 0xB7))
            {
                var field = MkvElements.Find(position, 0xF1)!;
                var target = ResolvePosition(field, targets);
                var trackId = MkvElements.Number(position, 0xF7);
                if (target.Identifier.EncodedValue != 0x1F43B675 || !_trackMap.ContainsKey(trackId))
                { throw new ContentFormatException(ErrorMessages.CueInvalid, ContentFormatErrorCode.InvalidStructure, field.Offset); }
                var relative = MkvElements.Number(position, 0xF0);
                if (relative >= (ulong)(_layout.OriginalLength(target) - target.Identifier.Length - target.SizeLength))
                { throw new ContentFormatException(ErrorMessages.CueInvalid, ContentFormatErrorCode.InvalidValue, position.Offset); }
                _positionTargets.Add(field, target);
                _cues.Add(new MkvCue(trackId, MkvElements.Number(point, 0xB3), target, MkvElements.Number(position, 0xEA) != 0));
            }
        }
        foreach (var head in MkvElements.All(Segment, 0x114D9B74))
        {
            foreach (var seek in MkvElements.All(head, 0x4DBB))
            {
                var field = MkvElements.Find(seek, 0x53AC)!;
                var target = ResolvePosition(field, targets);
                var id = ((BinarySegment)MkvElements.Find(seek, 0x53AB)!.Value!).ToArray(4);
                var expected = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(id);
                if (target.Identifier.EncodedValue != expected)
                { throw new ContentFormatException(ErrorMessages.SeekInvalid, ContentFormatErrorCode.InvalidStructure, field.Offset); }
                _positionTargets.Add(field, target);
            }
        }
        var unknown = Segment.IsUnknownSize;
        foreach (var cluster in _clusters) { unknown |= cluster.IsUnknownSize; }
        IsLive = unknown && _cues.Count == 0;
        ValidateMetadata();
        ValidateWebM();
    }

    private EbmlElement ResolvePosition(EbmlElement field, Dictionary<long, EbmlElement> targets)
    {
        var relative = (ulong)field.Value!;
        if (relative > (ulong)(long.MaxValue - SegmentDataOffset) || !targets.TryGetValue(SegmentDataOffset + (long)relative, out var target))
        { throw new ContentFormatException(ErrorMessages.SeekInvalid, ContentFormatErrorCode.InvalidValue, field.Offset); }
        return target;
    }

    private void MaintainPositions()
    {
        for (var pass = 0; pass < 64; pass++)
        {
            _layout.RemoveChangedChecksums(Ebml.Header.Element);
            foreach (var root in Ebml.Body.Elements) { _layout.RemoveChangedChecksums(root); }
            var offsets = new Dictionary<EbmlElement, ulong>();
            ulong offset = 0;
            foreach (var element in Segment.Children)
            { offsets.Add(element, offset); offset += (ulong)_layout.Measure(element); }
            var changed = false;
            foreach (var pair in _positionTargets)
            {
                if (!Contains(Segment, pair.Key)) { continue; }
                if (!offsets.TryGetValue(pair.Value, out var position)) { throw new NotSupportedException(ErrorMessages.PositionTargetRemoved); }
                if ((ulong)pair.Key.Value! != position) { _layout.Relocate(pair.Key, position); changed = true; }
            }
            if (!changed) { return; }
        }
        throw new ContentFormatException(ErrorMessages.LayoutLimit, ContentFormatErrorCode.LimitExceeded, Segment.Offset);
    }

    private static bool Contains(EbmlElement parent, EbmlElement value)
    {
        if (ReferenceEquals(parent, value)) { return true; }
        if (parent.Type == EbmlElementType.MasterElement)
        { foreach (var child in parent.Children) { if (Contains(child, value)) { return true; } } }
        return false;
    }

    private EbmlElement? GetChapters(bool create)
    {
        EnsureOpen();
        var chapters = MkvElements.Find(Segment, 0x1043A770);
        if (chapters is null && create)
        {
            chapters = EbmlElement.Create(VInt.FromEncoded(0x1043A770), EbmlElementType.MasterElement);
            Segment.Children.Add(chapters);
        }
        return chapters;
    }

    private static void ValidateChapterTimes(EbmlElement parent, bool ordered)
    {
        foreach (var chapter in MkvElements.All(parent, 0xB6))
        {
            var end = MkvElements.Find(chapter, 0x92);
            if (end?.Value is ulong timestamp && timestamp < MkvElements.Number(chapter, 0x91))
            { throw new ContentFormatException(ErrorMessages.ChapterTimeInvalid, ContentFormatErrorCode.InvalidValue, end.Offset); }
            if (ordered && end is null && MkvElements.Find(chapter, 0xB6) is null)
            { throw new ContentFormatException(ErrorMessages.ChapterEndRequired, ContentFormatErrorCode.InvalidStructure, chapter.Offset); }
            ValidateChapterTimes(chapter, ordered);
        }
    }

    private void ValidateMetadata()
    {
        // EBML's direct-parent schema applies recursive MinOccurs to the recursive parent too.
        // Keep recursion optional there and enforce the first occurrence at its actual parent here.
        foreach (var tags in MkvElements.All(Segment, 0x1254C367))
        {
            foreach (var tag in MkvElements.All(tags, 0x7373))
            {
                if (MkvElements.Find(tag, 0x67C8) is null)
                { throw new ContentFormatException(ErrorMessages.TagRequired, ContentFormatErrorCode.InvalidStructure, tag.Offset); }
            }
        }
        foreach (var edition in MkvElements.All(MkvElements.Find(Segment, 0x1043A770), 0x45B9))
        {
            if (MkvElements.Find(edition, 0xB6) is null)
            { throw new ContentFormatException(ErrorMessages.ChapterRequired, ContentFormatErrorCode.InvalidStructure, edition.Offset); }
            ValidateChapterTimes(edition, MkvElements.Number(edition, 0x45DD) != 0);
        }
    }

    private void ValidateWebM()
    {
        if (Ebml.Header.DocType != "webm") { return; }
        if (MkvElements.Find(Segment, 0x1941A469) is { } attachments)
        { throw new ContentFormatException(ErrorMessages.WebMUnsupported, ContentFormatErrorCode.Unsupported, attachments.Offset); }
        foreach (var track in _trackMap.Values)
        {
            var allowed = track.Kind switch
            {
                MediaTrackKind.Video => track.CodecId is "V_VP8" or "V_VP9" or "V_AV1",
                MediaTrackKind.Audio => track.CodecId is "A_VORBIS" or "A_OPUS",
                MediaTrackKind.Text => track.CodecId is "D_WEBVTT/SUBTITLES" or "D_WEBVTT/CAPTIONS",
                MediaTrackKind.Metadata => track.CodecId is "D_WEBVTT/DESCRIPTIONS" or "D_WEBVTT/METADATA",
                _ => false
            };
            if (!allowed) { throw new ContentFormatException(ErrorMessages.WebMUnsupported, ContentFormatErrorCode.Unsupported, track.Element.Offset); }
        }
    }

    private static void Preflight(Stream source, MkvReaderOptions options)
    {
        using var reader = EbmlReader.Create(source, MkvSchema.Matroska, options.Ebml, registry: MkvSchema.Registry);
        var tracks = 0;
        var cues = 0;
        var nodes = 0;
        Walk();
        void Walk()
        {
            while (reader.ReadNext())
            {
                var header = reader.Current;
                if (++nodes > options.Ebml.MaxElementCount || header.Identifier.EncodedValue == 0xAE && ++tracks > options.MaxTracks ||
                    header.Identifier.EncodedValue == 0xB7 && ++cues > options.MaxCuePoints)
                { throw new ContentFormatException(ErrorMessages.MetadataLimit, ContentFormatErrorCode.LimitExceeded, header.Offset); }
                if (header.Type == EbmlElementType.MasterElement) { reader.EnterMaster(); Walk(); reader.LeaveMaster(); }
                else { reader.Skip(); }
            }
        }
    }
}
