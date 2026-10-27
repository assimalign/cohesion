using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Protocol;

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
        : this(collectionId, name, new string[] { path }, false, objectId) { }

    /// <summary>Initializes an immutable index definition.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The ordinal index name.</param>
    /// <param name="paths">The ordered key paths.</param>
    /// <param name="isUnique">Whether valued scalar keys must be unique.</param>
    /// <param name="objectId">The physical generation identity.</param>
    /// <exception cref="ArgumentNullException">The paths are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The path count is outside 1..32.</exception>
    public DocumentIndexMetadata(Guid collectionId, string name, IReadOnlyList<string> paths, bool isUnique, ulong objectId)
        : this(collectionId, name, ScalarKeys(paths), isUnique, false, objectId) { }

    /// <summary>Initializes a typed index definition with at most one DISTINCT array key.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The ordinal index name.</param>
    /// <param name="keys">One to 32 ordered normalized keys.</param>
    /// <param name="isUnique">Whether valued scalar keys must be unique; array indexes cannot be unique.</param>
    /// <param name="includeMissing">Whether a MISSING leading key is indexed.</param>
    /// <param name="objectId">The physical generation identity.</param>
    /// <exception cref="ArgumentNullException">The key list or a key is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The key count is outside 1..32.</exception>
    /// <exception cref="DocumentCatalogException">Multiple array keys or a UNIQUE array index is requested (CD03025).</exception>
    public DocumentIndexMetadata(Guid collectionId, string name, IReadOnlyList<DocumentIndexKey> keys,
        bool isUnique, bool includeMissing, ulong objectId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys = Array.AsReadOnly(keys.ToArray());
        ArgumentOutOfRangeException.ThrowIfLessThan(keys.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keys.Count, 32);
        foreach (var key in keys) { ArgumentNullException.ThrowIfNull(key); }
        int arrays = keys.Count(key => key.ArraySource is not null);
        if (arrays > 1 || arrays != 0 && isUnique)
        { throw new DocumentCatalogException("CD03025", ErrorMessages.ArrayIndexDefinitionInvalid); }
        CollectionId = collectionId;
        Name = name;
        Keys = keys;
        // The scalar-only planner must not infer complete document coverage from a
        // scalar prefix of an array index (empty/filtered arrays have no postings).
        Paths = arrays == 0 ? Array.AsReadOnly(keys.Select(key => key.Path).ToArray()) : Array.Empty<string>();
        IsUnique = isUnique;
        IncludeMissing = includeMissing;
        ObjectId = objectId;
    }

    private static DocumentIndexKey[] ScalarKeys(IReadOnlyList<string> paths)
    { ArgumentNullException.ThrowIfNull(paths); return paths.Select(DocumentIndexKey.Scalar).ToArray(); }

    /// <summary>Gets the collection identity.</summary>
    public Guid CollectionId { get; }
    /// <summary>Gets the ordinal index name.</summary>
    public string Name { get; }
    /// <summary>Gets the leading key path, or an empty string for a default definition.</summary>
    public string Path => Paths is not { Count: > 0 } ? string.Empty : Paths[0];
    /// <summary>Gets ordered scalar paths, or an empty list for an array index.
    /// Array-aware consumers use <see cref="Keys"/>.</summary>
    public IReadOnlyList<string> Paths { get; }
    /// <summary>Gets the immutable ordered key descriptors.</summary>
    public IReadOnlyList<DocumentIndexKey> Keys { get; }
    /// <summary>Gets whether valued scalar keys must be unique.</summary>
    public bool IsUnique { get; }
    /// <summary>Gets whether documents with a MISSING leading key are indexed.</summary>
    public bool IncludeMissing { get; }
    /// <summary>Gets the physical generation identity.</summary>
    public ulong ObjectId { get; }

    /// <summary>Compares definitions, including their paths by value.</summary>
    /// <param name="other">The other definition.</param>
    /// <returns>Whether every definition field is equal.</returns>
    public bool Equals(DocumentIndexMetadata other) => CollectionId == other.CollectionId && Name == other.Name &&
        ObjectId == other.ObjectId && IsUnique == other.IsUnique && IncludeMissing == other.IncludeMissing &&
        (ReferenceEquals(Keys, other.Keys) || Keys is not null && other.Keys is not null && Keys.SequenceEqual(other.Keys));

    /// <summary>Computes a hash from the definition fields and path values.</summary>
    /// <returns>The value hash.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CollectionId); hash.Add(Name); hash.Add(ObjectId); hash.Add(IsUnique); hash.Add(IncludeMissing);
        if (Keys is not null) { foreach (var key in Keys) { hash.Add(key); } }
        return hash.ToHashCode();
    }
}
