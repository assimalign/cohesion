using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>An ordered, editable PDF array (§7.3.6).</summary>
public sealed class PdfArray : IPdfObject, IReadOnlyList<IPdfObject>
{
    private readonly List<IPdfObject> _items = new();
    internal long Revision { get; private set; }
    /// <summary>Creates an empty array.</summary>
    public PdfArray() { }
    /// <inheritdoc/>
    public int Count => _items.Count;
    /// <summary>Gets or replaces an item.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The stored object.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the array.</exception>
    /// <exception cref="ArgumentNullException">The new value is null; use PdfNull.</exception>
    public IPdfObject this[int index]
    {
        get => _items[index];
        set { ArgumentNullException.ThrowIfNull(value); _items[index] = value; Revision++; }
    }
    /// <summary>Appends an object.</summary>
    /// <param name="value">The object; use PdfNull for PDF null.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public void Add(IPdfObject value) { ArgumentNullException.ThrowIfNull(value); _items.Add(value); Revision++; }
    /// <summary>Removes an item.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the array.</exception>
    public void RemoveAt(int index) { _items.RemoveAt(index); Revision++; }
    /// <inheritdoc/>
    public IEnumerator<IPdfObject> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
