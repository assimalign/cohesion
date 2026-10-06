using System.Collections;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// The definitions a <see cref="GraphSchema"/> read returns, with the warnings the read reported.
/// </summary>
/// <typeparam name="T">The definition type.</typeparam>
/// <remarks>
/// A read that names a label the database does not have returns no definitions and a
/// <c>COHDBG010</c> warning instead of failing, so it never aborts the caller's explicit
/// transaction (Graph DESIGN, "Unknown labels and relationship types in reads"). Neo4j's schema API
/// likewise returns an empty list for a label token that does not exist.
/// </remarks>
public sealed class GraphSchemaResult<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _items;

    internal GraphSchemaResult(IReadOnlyList<T> items, IReadOnlyList<Diagnostic> diagnostics)
    {
        _items = items;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the read's warnings, in first-mention order; empty when it reported none.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Gets the number of definitions.</summary>
    public int Count => _items.Count;

    /// <summary>Gets a definition by index.</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The definition.</returns>
    public T this[int index] => _items[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
