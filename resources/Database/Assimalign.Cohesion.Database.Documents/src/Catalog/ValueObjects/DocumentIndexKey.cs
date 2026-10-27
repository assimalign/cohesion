using System;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>A normalized scalar path or DISTINCT array projection owned by the catalog.</summary>
/// <remarks>Array variable names are removed by binding. Element paths are relative to one element;
/// an empty element path denotes the element itself. No engine expression or callback is persisted.</remarks>
public sealed record DocumentIndexKey
{
    private DocumentIndexKey(string path, string? arraySource, DocumentIndexPredicate? when)
    {
        Path = path;
        ArraySource = arraySource;
        When = when;
    }

    /// <summary>Gets the document path, or the element-relative projection path for an array key.</summary>
    public string Path { get; }
    /// <summary>Gets the document path of the array source, or null for a scalar key.</summary>
    public string? ArraySource { get; }
    /// <summary>Gets the element-relative filter, or null to include every element.</summary>
    public DocumentIndexPredicate? When { get; }

    /// <summary>Creates a document-path key.</summary>
    /// <param name="path">The lossless dotted/bracket document path.</param>
    /// <returns>The immutable key descriptor.</returns>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public static DocumentIndexKey Scalar(string path)
    {
        DocumentIndexKeys.ValidatePath(path);
        return new DocumentIndexKey(path, null, null);
    }

    /// <summary>Creates a DISTINCT ARRAY projection with an optional WHEN filter.</summary>
    /// <param name="sourcePath">The document path of the array.</param>
    /// <param name="elementPath">The element-relative path, or empty for the element itself.</param>
    /// <param name="when">The element-relative filter; only TRUE admits an element.</param>
    /// <returns>The immutable key descriptor.</returns>
    /// <exception cref="ArgumentNullException">The element path is null.</exception>
    /// <exception cref="ArgumentException">A path is invalid.</exception>
    public static DocumentIndexKey DistinctArray(string sourcePath, string elementPath = "", DocumentIndexPredicate? when = null)
    {
        DocumentIndexKeys.ValidatePath(sourcePath);
        ArgumentNullException.ThrowIfNull(elementPath);
        if (elementPath.Length != 0) { DocumentIndexKeys.ValidatePath(elementPath); }
        return new DocumentIndexKey(elementPath, sourcePath, when);
    }
}
