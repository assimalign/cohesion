using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>An editable dictionary preserving insertion order and decoded name equality (§7.3.7).</summary>
public sealed class PdfDictionary : IPdfObject, IReadOnlyDictionary<PdfName, IPdfObject>
{
    private readonly OrderedDictionary<PdfName, IPdfObject> _items = new();
    internal long Revision { get; private set; }
    /// <summary>Creates an empty dictionary.</summary>
    public PdfDictionary() { }
    /// <inheritdoc/>
    public int Count => _items.Count;
    /// <inheritdoc/>
    public IEnumerable<PdfName> Keys => _items.Keys;
    /// <inheritdoc/>
    public IEnumerable<IPdfObject> Values => _items.Values;
    /// <summary>Gets or sets an entry; replacement preserves its insertion position.</summary>
    /// <param name="key">The decoded name.</param>
    /// <returns>The stored object.</returns>
    /// <exception cref="ArgumentNullException">The key or new value is null.</exception>
    /// <exception cref="KeyNotFoundException">The requested key does not exist.</exception>
    public IPdfObject this[PdfName key]
    {
        get => _items[key];
        set { ArgumentNullException.ThrowIfNull(value); _items[key] = value; Revision++; }
    }
    /// <inheritdoc/>
    public bool ContainsKey(PdfName key) => _items.ContainsKey(key);
    /// <inheritdoc/>
    public bool TryGetValue(PdfName key, [MaybeNullWhen(false)] out IPdfObject value) => _items.TryGetValue(key, out value);
    /// <summary>Removes an entry.</summary>
    /// <param name="key">The decoded name.</param>
    /// <returns>Whether an entry was removed.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    public bool Remove(PdfName key)
    {
        if (!_items.Remove(key)) { return false; }
        Revision++; return true;
    }
    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<PdfName, IPdfObject>> GetEnumerator() => _items.GetEnumerator();
    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal IPdfObject? Get(string key) => _items.TryGetValue(PdfName.Create(Encoding.ASCII.GetBytes(key)), out var value) ? value : null;
}
