using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

public sealed partial class PdfDocument
{
    private readonly Dictionary<PdfObjectIdentifier, PdfObjectState> _originalStates = new();
    private readonly HashSet<int> _changed = new();
    private PdfObjectState? _trailerState;

    /// <summary>Allocates a new generation-zero indirect object, without reusing free numbers (§7.3.10).</summary>
    /// <param name="value">The object to retain.</param>
    /// <returns>The allocated identity, suitable for PdfIndirectReference.Create.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">The object-number or reader object limit is exhausted.</exception>
    public PdfObjectIdentifier Add(IPdfObject value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        var number = NextObjectNumber;
        if (number > int.MaxValue || _entries.Count >= _options.MaxObjects)
        {
            throw new ContentFormatException(ErrorMessages.WriterLimit, ContentFormatErrorCode.LimitExceeded, AbsoluteOrigin);
        }
        var identifier = PdfObjectIdentifier.Create((int)number);
        _entries.Add(identifier.Number, new(identifier, PdfCrossReferenceKind.Uncompressed, 0, 0, 0, 0));
        _cache.Add(identifier, value); _changed.Add(identifier.Number);
        return identifier;
    }
    /// <summary>Replaces a current live object while preserving its number and generation (§7.5.6).</summary>
    /// <param name="identifier">The current live identity.</param>
    /// <param name="value">The replacement object.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The identity is missing, free, zero or obsolete.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public void Replace(PdfObjectIdentifier identifier, IPdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireLive(identifier);
        _cache[identifier] = value; _changed.Add(identifier.Number); _objectStream = null;
    }
    /// <summary>Frees a live object and increments its generation, saturating at 65535 (§7.5.4).</summary>
    /// <param name="identifier">The current live identity.</param>
    /// <exception cref="ArgumentException">The identity is missing, free, zero or obsolete.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public void Free(PdfObjectIdentifier identifier)
    {
        RequireLive(identifier);
        var freed = PdfObjectIdentifier.Create(identifier.Number, Math.Min(65535, identifier.Generation + 1));
        _entries[identifier.Number] = new(freed, PdfCrossReferenceKind.Free, 0, 0, 0, 0);
        _cache.Remove(identifier); _originalStates.Remove(identifier); _changed.Add(identifier.Number); _objectStream = null;
    }
    private void RequireLive(PdfObjectIdentifier identifier)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (identifier.Number == 0 || !_entries.TryGetValue(identifier.Number, out var entry) ||
            entry.Kind == PdfCrossReferenceKind.Free || entry.Identifier != identifier)
        {
            throw new ArgumentException(ErrorMessages.ObjectNotLive, nameof(identifier));
        }
    }
    internal long NextObjectNumber
    {
        get
        {
            long next = 1;
            if (Trailer.Get("Size") is PdfInteger size && size.Value > next) { next = size.Value; }
            foreach (var number in _entries.Keys) { next = Math.Max(next, (long)number + 1); }
            return next;
        }
    }
    internal bool IsChanged(PdfObjectIdentifier identifier) => _changed.Contains(identifier.Number) ||
        _originalStates.TryGetValue(identifier, out var state) && state.IsChanged;
    internal bool HasEdits
    {
        get
        {
            if (_changed.Count != 0 || _trailerState?.IsChanged == true) { return true; }
            foreach (var state in _originalStates.Values) { if (state.IsChanged) { return true; } }
            return false;
        }
    }
}
