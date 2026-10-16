using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

public sealed partial class PdfDocument
{
    private PdfReaderOptions _options = new();
    private PdfSecurityState _security = new();
    private CancellationToken _loadCancellationToken;
    private readonly Dictionary<PdfObjectIdentifier, IPdfObject> _cache = new();
    private readonly HashSet<PdfObjectIdentifier> _resolving = new();
    private PdfObjectIdentifier _objectStreamIdentifier;
    private PdfObjectStream? _objectStream;
    private bool _recovered;
    private long AbsoluteOrigin => SourceOffset + (_source is ContentSliceStream slice ? slice.SourceOffset : 0);

    internal void Initialize(PdfReaderOptions options, PdfSecurityState security, CancellationToken cancellationToken)
    {
        _options = options; _security = security; _loadCancellationToken = cancellationToken; _recovered = StartXref < 0;
        try
        {
            // Encrypted object streams may contain the catalog; xrefs/trailer must still load.
            if (!IsEncrypted) { UpdateVersion(); }
        }
        finally { _loadCancellationToken = default; }
    }
    private void UpdateVersion()
    {
        if (Catalog.Get("Version") is PdfName name)
        {
            var text = Encoding.ASCII.GetString(name.Bytes.Span);
            if (!System.Version.TryParse(text, out var version) || version.Build != -1 ||
                !((version.Major == 1 && version.Minor is >= 0 and <= 7) || (version.Major == 2 && version.Minor == 0)))
            {
                throw new ContentFormatException(ErrorMessages.HeaderInvalid, ContentFormatErrorCode.Unsupported, AbsoluteOrigin);
            }
            if (version > Version) { Version = version; }
        }
    }
    /// <summary>Gets the catalog dictionary through the current trailer's Root reference (§7.7.2).</summary>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">The catalog is malformed or requires unsupported encryption.</exception>
    public PdfDictionary Catalog
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var catalog = ResolveValue(Trailer.Get("Root") ?? new PdfNull()) as PdfDictionary;
            if (catalog is null || !PdfFileLoader.IsName(catalog.Get("Type"), "Catalog"))
            {
                throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin);
            }
            return catalog;
        }
    }
    /// <summary>Resolves and caches an indirect object (§7.3.10 and §7.5). Missing, free or obsolete generations resolve to PdfNull.</summary>
    /// <param name="identifier">The nonzero identity to resolve.</param>
    /// <returns>The cached object instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The object number is zero.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">The object is malformed, cyclic, over a limit or requires unsupported decoding.</exception>
    public IPdfObject Resolve(PdfObjectIdentifier identifier)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(identifier.Number);
        try { return ResolveCore(identifier); }
        catch (ContentFormatException error) when (_options.IsLenient && !_recovered && _resolving.Count == 0 && error.ErrorCode == ContentFormatErrorCode.InvalidStructure)
        {
            _recovered = true;
            var (entries, trailer) = new PdfFileLoader(_source, SourceOffset, _options, _loadCancellationToken, _security).Rebuild();
            _entries.Clear();
            foreach (var pair in entries) { _entries.Add(pair.Key, pair.Value); }
            foreach (var pair in trailer) { Trailer[pair.Key] = pair.Value; }
            _cache.Clear(); _objectStream = null; StartXref = -1;
            _security.IsEncrypted = IsEncrypted;
            return ResolveCore(identifier);
        }
    }
    private IPdfObject ResolveCore(PdfObjectIdentifier identifier)
    {
        _loadCancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(identifier, out var cached)) { return cached; }
        if (!_entries.TryGetValue(identifier.Number, out var entry) || entry.Kind == PdfCrossReferenceKind.Free || entry.Identifier != identifier) { return new PdfNull(); }
        if (_resolving.Count >= _options.MaxDepth) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, AbsoluteOrigin + entry.Offset); }
        if (!_resolving.Add(identifier)) { throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin + entry.Offset); }
        try
        {
            IPdfObject value;
            if (entry.Kind == PdfCrossReferenceKind.Uncompressed)
            {
                using var input = new PdfInput(_source, SourceOffset + entry.Offset, cancellationToken: _loadCancellationToken);
                var parser = new PdfParser(input, _options, id => ResolveValue(Resolve(id)), () => _security.IsEncrypted);
                value = parser.ReadIndirect(identifier).Value;
                if (value is PdfStream stream) { stream.Configure(_options, ResolveValue); }
            }
            else
            {
                if (!_entries.TryGetValue(entry.ObjectStreamNumber, out var containerEntry) || containerEntry.Kind != PdfCrossReferenceKind.Uncompressed)
                {
                    throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin);
                }
                if (_objectStream is null || _objectStreamIdentifier != containerEntry.Identifier)
                {
                    if (Resolve(containerEntry.Identifier) is not PdfStream stream) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin + containerEntry.Offset); }
                    _objectStream = new PdfObjectStream(stream, _options); _objectStreamIdentifier = containerEntry.Identifier;
                }
                value = _objectStream.Extract(entry.ObjectStreamIndex, identifier.Number, _options, () => _security.IsEncrypted);
            }
            _cache.Add(identifier, value);
            return value;
        }
        finally { _resolving.Remove(identifier); }
    }
    internal IPdfObject ResolveValue(IPdfObject value)
    {
        var depth = 0;
        var visited = new HashSet<PdfObjectIdentifier>();
        while (value is PdfIndirectReference reference)
        {
            if (++depth > _options.MaxDepth) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, AbsoluteOrigin); }
            if (!visited.Add(reference.Identifier)) { throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin); }
            value = Resolve(reference.Identifier);
        }
        return value;
    }
}
