using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>An editable still-image view over a BMFF document, retaining lazy image extents.</summary>
/// <remarks>Access is sequential. Keep all borrowed sources open and unchanged through saving.
/// Direct tree edits require constructing a new view before any save relocates its coordinates.</remarks>
[global::System.Diagnostics.DebuggerDisplay("HeifDocument")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed partial class HeifDocument : IDisposable
{
    private readonly HeifReaderOptions _options;
    private readonly bool _ownsDocument;
    private readonly List<HeifItem> _items = new();
    private readonly Dictionary<uint, HeifItem> _byId = new();
    private readonly List<ContentFormatException> _diagnostics = new();
    private readonly List<HeifSegmentStream> _ownedStreams = new();
    private readonly List<BinarySegment> _appendedData = new();
    private MetaBox? _meta;
    private ItemInfoBox? _info;
    private ItemLocationBox? _locations;
    private ItemPropertiesBox? _properties;
    private ItemPropertyContainerBox? _propertyContainer;
    private ItemReferenceBox? _references;
    private ItemDataBox? _idat;
    private BinarySegment _originalIdat;
    private bool _disposed;
    private bool _edited;

    private HeifDocument(BmffDocument document, HeifReaderOptions options, bool ownsDocument)
    { Document = document; _options = options; _ownsDocument = ownsDocument; }

    /// <summary>Gets the borrowed or owned BMFF tree.</summary>
    public BmffDocument Document { get; }
    /// <summary>Gets the items in iinf order.</summary>
    public IReadOnlyList<HeifItem> Items => _items.AsReadOnly();
    /// <summary>Gets diagnostics collected by lenient validation.</summary>
    public IReadOnlyList<ContentFormatException> Diagnostics => _diagnostics.AsReadOnly();
    /// <summary>Gets the primary item, or null when absent.</summary>
    public HeifItem? PrimaryItem { get; private set; }
    /// <summary>Gets the primary image; its Grid exposes tiles when applicable.</summary>
    public HeifItem? PrimaryImage => PrimaryItem is { IsImage: true } ? PrimaryItem : null;
    /// <summary>Gets whether an AVIF brand was declared.</summary>
    public bool IsAvif { get; private set; }

    /// <summary>Creates a view over a loaded BMFF tree using the HEIF registry; the tree stays caller-owned.</summary>
    /// <param name="document">The loaded tree and its byte-stable source.</param>
    /// <param name="options">Limits and validation policy.</param>
    /// <returns>The item view.</returns>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is invalid.</exception>
    /// <exception cref="ContentFormatException">Required structure is invalid in strict mode or a limit is exceeded.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public static HeifDocument Create(BmffDocument document, HeifReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new HeifReaderOptions();
        options.Validate();
        var result = new HeifDocument(document, options, false);
        result.ReadItems();
        return result;
    }

    /// <summary>Loads a seekable source through BMFF and constructs the HEIF item view.</summary>
    /// <param name="source">The readable, seekable source at the first box.</param>
    /// <param name="options">Limits and validation policy.</param>
    /// <param name="leaveOpen">Whether disposing the view leaves the source open.</param>
    /// <returns>The loaded document.</returns>
    /// <exception cref="ArgumentException">The source or options are invalid.</exception>
    /// <exception cref="ContentFormatException">The input is malformed, unsupported in strict mode or exceeds a limit.</exception>
    /// <exception cref="System.ArgumentNullException">The source is null.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public static HeifDocument Load(Stream source, HeifReaderOptions? options = null, bool leaveOpen = true)
    {
        options ??= new HeifReaderOptions();
        options.Validate();
        var registry = HeifBoxRegistry.Register(BmffBoxRegistry.CreateBuilder(), options).Build();
        var document = BmffDocument.Load(source, BmffOptions(options), registry, leaveOpen);
        try
        {
            var result = new HeifDocument(document, options, true);
            result.ReadItems();
            return result;
        }
        catch { document.Dispose(); throw; }
    }

    internal static BmffReaderOptions BmffOptions(HeifReaderOptions options) => new()
    {
        MaxBufferedPayloadSize = options.MaxBufferedPayloadSize,
        MaxDepth = options.MaxDepth,
        MaxTableEntryCount = Math.Min(options.MaxItemCount, options.MaxExtentCount),
        // Unknown nonessential properties must survive even under strict profile validation.
        Strict = false
    };

    /// <summary>Looks up an item without decoding its payload.</summary>
    /// <param name="id">The item ID.</param>
    /// <returns>The item, or null.</returns>
    public HeifItem? GetItem(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>Gets thumbnail items that reference an image.</summary>
    /// <param name="imageId">The master image ID.</param>
    /// <returns>Thumbnails in item order.</returns>
    public IReadOnlyList<HeifItem> GetThumbnails(uint imageId) => GetIncoming(imageId, 0x74686d62, false);

    /// <summary>Gets alpha auxiliary images that reference an image and carry a recognized alpha URN.</summary>
    /// <param name="imageId">The master image ID.</param>
    /// <returns>Alpha images in item order.</returns>
    public IReadOnlyList<HeifItem> GetAlphaImages(uint imageId) => GetIncoming(imageId, 0x6175786c, true);

    /// <summary>Gets all Exif metadata items, with TIFF-relative data exposed on each item.</summary>
    public IReadOnlyList<HeifItem> ExifItems => MetadataItems(false);
    /// <summary>Gets all application/rdf+xml MIME metadata items.</summary>
    public IReadOnlyList<HeifItem> XmpItems => MetadataItems(true);

    /// <summary>Saves through BMFF's forward-only writer and iloc relocation.</summary>
    /// <param name="output">A separate borrowed output stream; never alias a segment source.</param>
    /// <exception cref="ContentFormatException">The edit cannot be relocated safely.</exception>
    /// <exception cref="InvalidOperationException">The output is the original source.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    /// <exception cref="System.ArgumentNullException">The output is null.</exception>
    /// <exception cref="System.ArgumentException">The output is not writable.</exception>
    /// <exception cref="System.IO.IOException">Reading retained data or writing the output fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public void Save(Stream output) { ValidateSave(); CompactDeletedData(output); Document.Save(output); }

    /// <summary>Saves with BMFF asynchronous output; external profile box writes remain synchronous.</summary>
    /// <param name="output">A separate borrowed output.</param>
    /// <param name="cancellationToken">Cancellation for the save.</param>
    /// <returns>A task completing after the save.</returns>
    /// <exception cref="ContentFormatException">The edit cannot be relocated safely.</exception>
    /// <exception cref="InvalidOperationException">The output is the original source.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    /// <exception cref="System.ArgumentNullException">The output is null.</exception>
    /// <exception cref="System.ArgumentException">The output is not writable.</exception>
    /// <exception cref="System.IO.IOException">Reading retained data or writing the output fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public ValueTask SaveAsync(Stream output, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); ValidateSave(); CompactDeletedData(output); return Document.SaveAsync(output, cancellationToken); }

    /// <summary>Releases local segment views and the document/source only when this view loaded it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var stream in _ownedStreams)
        {
            stream.Dispose();
        }

        if (_ownsDocument)
        {
            Document.Dispose();
        }
    }

    private void Report(string message, ContentFormatErrorCode code, long position, HeifItem? item = null)
    {
        if (item is not null)
        {
            item.IsSupported = false;
        }

        var error = new ContentFormatException(message, code, position);
        if (_options.Strict || code == ContentFormatErrorCode.LimitExceeded)
        {
            throw error;
        }

        _diagnostics.Add(error);
    }

    private IReadOnlyList<HeifItem> MetadataItems(bool xmp)
    {
        var result = new List<HeifItem>();
        foreach (var item in _items)
        {
            if (xmp ? IsXmp(item) : item.Type.Value == 0x45786966)
            {
                result.Add(item);
            }
        }

        return result.AsReadOnly();
    }
    private static bool IsXmp(HeifItem item) => item.Type.Value == 0x6d696d65 && item.ContentType == "application/rdf+xml";
    private IReadOnlyList<HeifItem> GetIncoming(uint id, uint type, bool alpha)
    {
        var result = new List<HeifItem>();
        foreach (var item in _items)
        {
            var matches = false;
            foreach (var reference in item.ReferenceList)
            {
                if (reference.Type.Value == type)
                {
                    foreach (var target in reference.ToItemIds)
                    {
                        matches |= target == id;
                    }
                }
            }

            if (!matches)
            {
                continue;
            }

            if (!alpha) { result.Add(item); continue; }
            foreach (var property in item.PropertyList)
            {
                if (property.Box is AuxiliaryTypeBox auxiliary && auxiliary.AuxiliaryType is "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha" or "urn:mpeg:hevc:2015:auxid:1")
                { result.Add(item); break; }
            }
        }
        return result.AsReadOnly();
    }

    private sealed class DebugView
    {
        private readonly HeifDocument _target;

        public DebugView(HeifDocument value)
        {
            _target = value;
        }

        public BmffDocument Document => _target.Document;
        public HeifItem? PrimaryItem => _target.PrimaryItem;
        public HeifItem? PrimaryImage => _target.PrimaryImage;
        public bool IsAvif => _target.IsAvif;
    }
}
