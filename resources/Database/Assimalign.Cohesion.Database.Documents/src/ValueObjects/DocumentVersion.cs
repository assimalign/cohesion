using System;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// A monotonically increasing document version used for optimistic concurrency.
/// </summary>
/// <param name="Value">The underlying version number; increments on every write.</param>
public readonly record struct DocumentVersion(ulong Value) : IComparable<DocumentVersion>
{
    /// <inheritdoc />
    public int CompareTo(DocumentVersion other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
