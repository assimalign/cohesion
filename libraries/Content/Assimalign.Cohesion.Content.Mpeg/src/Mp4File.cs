using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>A finite MP4 media view over an editable BMFF document and its borrowed source.</summary>
/// <remarks>Use the BMFF reader/writer for forward-only streams. Tracks and indexes require the source and
/// native tree to remain unchanged; reload the saved output after edits or relocation. Configuration and media
/// payloads remain lazy. Access to a shared source is sequential.</remarks>
public sealed class Mp4File : IMediaFile
{
    private static readonly ContentFormat _format = new()
    {
        Name = "MP4", Kind = ContentKind.Media, MediaTypes = ["video/mp4", "audio/mp4", "application/mp4"],
        FileExtensions = [".mp4", ".m4a", ".m4v"], Specification = "ISO/IEC 14496-14"
    };
    private readonly long _origin;
    private readonly long _length;
    private bool _disposed;
    private Mp4File(BmffDocument document, long origin, Mp4ReaderOptions options, CancellationToken token)
    {
        Document = document;
        _origin = origin;
        _length = document.Source!.Length - origin;
        var movie = Mp4Tree.Child<MovieBox>(document.Boxes) ??
            throw new ContentFormatException(ErrorMessages.RequiredBoxMissing, ContentFormatErrorCode.InvalidStructure, origin);
        var header = Mp4Tree.Required<MovieHeaderBox>(movie);
        var brands = Mp4Tree.Child<FileTypeBox>(document.Boxes) ??
            throw new ContentFormatException(ErrorMessages.RequiredBoxMissing, ContentFormatErrorCode.InvalidStructure, origin);
        MajorBrand = FourCC.FromUInt32(brands.MajorBrand);
        var compatible = new FourCC[brands.CompatibleBrands.Length];
        BrandFeatures = Mp4Profile.InterpretBrand(MajorBrand);
        for (var i = 0; i < compatible.Length; i++)
        {
            compatible[i] = FourCC.FromUInt32(brands.CompatibleBrands[i]);
            BrandFeatures |= Mp4Profile.InterpretBrand(compatible[i]);
        }
        CompatibleBrands = Array.AsReadOnly(compatible);
        var tracks = new List<IMediaTrack>();
        var native = new Dictionary<uint, Mp4Track>();
        var extension = Mp4Tree.Child<MovieExtensionBox>(movie.Children);
        foreach (var box in movie.Children)
        {
            token.ThrowIfCancellationRequested();
            if (box is not TrackBox track) { continue; }
            if (tracks.Count >= options.MaxTrackCount)
            {
                throw new ContentFormatException(ErrorMessages.TrackLimit, ContentFormatErrorCode.LimitExceeded, track.Offset);
            }
            var tkhd = Mp4Tree.Required<TrackHeaderBox>(track);
            var mdia = Mp4Tree.Required<MediaBox>(track);
            var mdhd = Mp4Tree.Required<MediaHeaderBox>(mdia);
            var handler = Mp4Tree.Required<HandlerBox>(mdia);
            var info = Mp4Tree.Required<MediaInfoBox>(mdia);
            var table = Mp4Tree.Required<SampleTableBox>(info);
            var descriptions = Mp4Tree.Required<SampleDescriptionBox>(table);
            if (descriptions.Children.Count == 0)
            {
                throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, descriptions.Offset);
            }
            var mapping = Mp4Tree.Required<SampleToChunkBox>(table);
            var description = mapping.Entries.Length == 0 ? 1u : mapping.Entries[0].SampleDescriptionIndex;
            if (mapping.Entries.Length == 0 && extension is not null)
            {
                foreach (var item in extension.Children)
                {
                    if (item is TrackExtensionBox trex && trex.TrackId == tkhd.TrackId) { description = trex.DefaultSampleDescriptionIndex; }
                }
            }
            if (description == 0 || description > descriptions.Children.Count)
            {
                throw new ContentFormatException(ErrorMessages.SampleTablesInvalid, ContentFormatErrorCode.InvalidStructure, descriptions.Offset);
            }
            var entry = descriptions.Children[(int)description - 1];
            var local = IsLocalEntry(entry, info);
            // All descriptions must remain local before any byte-coordinate operation is advertised.
            foreach (var candidate in descriptions.Children) { local &= IsLocalEntry(candidate, info); }
            var edits = new Mp4EditMap(Mp4Tree.Child<EditListBox>(Mp4Tree.Child<EditBox>(track.Children)?.Children ?? Array.Empty<BmffBox>()), header.TimeScale, mdhd.TimeScale);
            var index = new Mp4SampleIndex(tkhd.TrackId, mdhd.TimeScale, table, edits, local, document.Boxes, token);
            Mp4Track model = handler.HandlerType.ToString() switch
            {
                "vide" => new Mp4VideoTrack(tkhd, mdhd, handler, entry, index, header.TimeScale),
                "soun" => new Mp4AudioTrack(tkhd, mdhd, handler, entry, index, header.TimeScale),
                _ => new Mp4Track(tkhd, mdhd, handler, entry, index, header.TimeScale)
            };
            model.IsLocal = local;
            model.DescriptionCount = (uint)descriptions.Children.Count;
            model.DescriptionIndex = description;
            if (!native.TryAdd(tkhd.TrackId, model))
            {
                throw new ContentFormatException(ErrorMessages.DuplicateTrack, ContentFormatErrorCode.InvalidStructure, tkhd.Offset);
            }
            tracks.Add(model);
        }
        if (tracks.Count == 0) { throw new ContentFormatException(ErrorMessages.RequiredBoxMissing, ContentFormatErrorCode.InvalidStructure, movie.Offset); }
        Mp4Fragments.Bind(document, movie, native, options, token);
        Tracks = tracks.AsReadOnly();
        IsFragmented = Mp4Profile.IsFragmented(document);
        var duration = new MediaTimestamp(0, header.TimeScale);
        foreach (var track in tracks)
        {
            if (track.Duration is { } value && value > duration) { duration = value; }
        }
        var mehd = extension is null ? null : Mp4Tree.Child<MovieExtensionHeaderBox>(extension.Children);
        if (mehd is not null && mehd.FragmentDuration != 0 && mehd.FragmentDuration != (mehd.Version == 0 ? uint.MaxValue : ulong.MaxValue))
        {
            duration = Mp4Track.Timestamp(mehd.FragmentDuration, header.TimeScale, mehd.Offset);
        }
        if (header.Duration != 0 && header.Duration != (header.Version == 0 ? uint.MaxValue : ulong.MaxValue))
        {
            duration = Mp4Track.Timestamp(header.Duration, header.TimeScale, header.Offset);
        }
        Duration = duration;
        Diagnostics = Mp4Conformance.Check(this, movie, brands, options.Strict, token);
    }

    /// <summary>Loads the profile and validates native table relationships without expanding samples.</summary>
    /// <param name="source">A readable, seekable source at the first box.</param>
    /// <param name="options">Container limits, profile policy and discovery caps.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open; true by default.</param>
    /// <param name="cancellationToken">Cancellation for bounded parsing and validation scans.</param>
    /// <returns>The caller-owned media view.</returns>
    /// <exception cref="ArgumentException">The source or limits are invalid.</exception>
    /// <exception cref="ContentFormatException">The input is malformed, unsupported or exceeds limits.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static Mp4File Load(Stream source, Mp4ReaderOptions? options = null, bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var limits = options ?? new Mp4ReaderOptions();
        ArgumentNullException.ThrowIfNull(limits.ContainerOptions);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxTrackCount);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxFragmentRunCount);
        var origin = source.CanSeek ? source.Position : 0;
        var document = Mp4Profile.LoadDocument(source, limits.ContainerOptions, leaveOpen, cancellationToken);
        try { return new Mp4File(document, origin, limits, cancellationToken); }
        catch { document.Dispose(); throw; }
    }

    /// <summary>Loads with cancellation; metadata parsing follows BMFF's synchronous bounded I/O contract.</summary>
    /// <param name="source">A readable, seekable source.</param>
    /// <param name="options">Limits and policy.</param>
    /// <param name="leaveOpen">Whether the source is borrowed.</param>
    /// <param name="cancellationToken">Cancellation for parsing and validation.</param>
    /// <returns>A synchronously completed media-view task.</returns>
    /// <exception cref="ArgumentException">The source or limits are invalid.</exception>
    /// <exception cref="ContentFormatException">The input is malformed, unsupported or exceeds limits.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static ValueTask<Mp4File> LoadAsync(Stream source, Mp4ReaderOptions? options = null, bool leaveOpen = true, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Load(source, options, leaveOpen, cancellationToken));

    /// <summary>Gets the editable tree. Stop using this view's tracks before changing or saving it.</summary>
    public BmffDocument Document { get; }
    /// <summary>Gets the raw major brand.</summary>
    public FourCC MajorBrand { get; }
    /// <summary>Gets the compatible brands in wire order, including unrecognized codes.</summary>
    public IReadOnlyList<FourCC> CompatibleBrands { get; }
    /// <summary>Gets the union of retained brand declarations.</summary>
    public Mp4BrandFeatures BrandFeatures { get; }
    /// <summary>Gets profile violations retained in lenient mode; structural parsing errors always throw.</summary>
    public IReadOnlyList<ContentFormatException> Diagnostics { get; }
    /// <summary>Gets whether a movie-extends declaration or movie fragments are present.</summary>
    public bool IsFragmented { get; }
    /// <inheritdoc/>
    public IReadOnlyList<IMediaTrack> Tracks { get; }
    /// <inheritdoc/>
    public MediaTimestamp? Duration { get; }
    /// <inheritdoc/>
    public bool IsLive => false;
    /// <inheritdoc/>
    public string? Name => null;
    /// <inheritdoc/>
    public ContentFormat Format => _format;
    /// <inheritdoc/>
    public string? MediaType => (BrandFeatures & Mp4BrandFeatures.AudioOnly) != 0 ? "audio/mp4" : "video/mp4";
    /// <inheritdoc/>
    public long? Length => _length;
    /// <inheritdoc/>
    public bool IsReadOnly => true;
    /// <inheritdoc/>
    public bool CanReopen => true;
    /// <inheritdoc/>
    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ContentSliceStream.Create(Document.Source!, _origin, _length);
    }
    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(OpenRead());
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed) { _disposed = true; Document.Dispose(); }
    }
    /// <inheritdoc/>
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    internal static bool IsLocalEntry(BmffBox entry, MediaInfoBox info)
    {
        if (entry is not IMp4SampleEntry known) { return false; }
        var references = Mp4Tree.Required<DataReferenceBox>(Mp4Tree.Required<DataInfoBox>(info));
        var index = known.DataReferenceIndex;
        if (index == 0 || index > references.Children.Count)
        {
            throw new ContentFormatException(ErrorMessages.DataReferenceInvalid, ContentFormatErrorCode.InvalidStructure, entry.Offset);
        }
        return references.Children[index - 1] is DataEntryUrlBox url && (url.Flags & 1) != 0;
    }
}
