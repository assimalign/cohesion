using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Describes a secondary index with ordered, case-sensitive document key paths.</summary>
public readonly record struct DocumentIndexMetadata
{
    /// <summary>Initializes a single-path nonunique index definition.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The ordinal index name.</param>
    /// <param name="path">The leading path.</param>
    /// <param name="objectId">The physical generation identity.</param>
    public DocumentIndexMetadata(Guid collectionId, string name, string path, ulong objectId)
        : this(collectionId, name, [path], false, objectId) { }

    /// <summary>Initializes an immutable index definition.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The ordinal index name.</param>
    /// <param name="paths">The ordered key paths.</param>
    /// <param name="isUnique">Whether valued scalar keys must be unique.</param>
    /// <param name="objectId">The physical generation identity.</param>
    /// <exception cref="ArgumentNullException">The paths are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The path count is outside 1..32.</exception>
    public DocumentIndexMetadata(Guid collectionId, string name, IReadOnlyList<string> paths, bool isUnique, ulong objectId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(paths.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paths.Count, 32);
        CollectionId = collectionId;
        Name = name;
        Paths = Array.AsReadOnly(paths.ToArray());
        IsUnique = isUnique;
        ObjectId = objectId;
    }

    /// <summary>Gets the collection identity.</summary>
    public Guid CollectionId { get; }
    /// <summary>Gets the ordinal index name.</summary>
    public string Name { get; }
    /// <summary>Gets the leading key path, or an empty string for a default definition.</summary>
    public string Path => Paths is null ? string.Empty : Paths[0];
    /// <summary>Gets an immutable snapshot of the ordered paths.</summary>
    public IReadOnlyList<string> Paths { get; }
    /// <summary>Gets whether valued scalar keys must be unique.</summary>
    public bool IsUnique { get; }
    /// <summary>Gets the physical generation identity.</summary>
    public ulong ObjectId { get; }

    /// <summary>Compares definitions, including their paths by value.</summary>
    /// <param name="other">The other definition.</param>
    /// <returns>Whether every definition field is equal.</returns>
    public bool Equals(DocumentIndexMetadata other) => CollectionId == other.CollectionId && Name == other.Name &&
        ObjectId == other.ObjectId && IsUnique == other.IsUnique &&
        (ReferenceEquals(Paths, other.Paths) || Paths is not null && other.Paths is not null && Paths.SequenceEqual(other.Paths));

    /// <summary>Computes a hash from the definition fields and path values.</summary>
    /// <returns>The value hash.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CollectionId); hash.Add(Name); hash.Add(ObjectId); hash.Add(IsUnique);
        if (Paths is not null) { foreach (string path in Paths) { hash.Add(path, StringComparer.Ordinal); } }
        return hash.ToHashCode();
    }
}
