using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// A <c>CREATE INDEX</c> statement that creates an index over ordered document paths.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("OqlCreateIndexExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlCreateIndexExpression.DebugView))]
public sealed class OqlCreateIndexExpression : OqlExpression
{
    /// <summary>Initializes a document index creation statement.</summary>
    /// <param name="indexName">The name of the index to create.</param>
    /// <param name="collection">The collection name; the executor rejects reserved system collections as read-only.</param>
    /// <param name="path">The document path indexed within the collection.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    public OqlCreateIndexExpression(
        string indexName,
        string collection,
        OqlPathExpression path,
        Location? location = null) : this(indexName, collection, [path], false, false, location) { }

    /// <summary>Initializes a composite index creation statement.</summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="collection">The collection name.</param>
    /// <param name="paths">The ordered document key paths.</param>
    /// <param name="isUnique">Whether valued scalar keys must be unique.</param>
    /// <param name="ifNotExists">Whether an existing name is a no-op.</param>
    /// <param name="location">The statement span.</param>
    /// <exception cref="ArgumentNullException">The paths are null.</exception>
    public OqlCreateIndexExpression(string indexName, string collection, IReadOnlyList<OqlPathExpression> paths,
        bool isUnique, bool ifNotExists, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(paths);
        IndexName = indexName;
        Collection = collection;
        Paths = Array.AsReadOnly(paths.ToArray());
        IsUnique = isUnique;
        IfNotExists = ifNotExists;
    }

    /// <summary>Gets the name of the index to create.</summary>
    public string IndexName { get; }

    /// <summary>Gets the collection name; database qualification is never accepted.</summary>
    public string Collection { get; }

    /// <summary>Gets the document path indexed within the collection.</summary>
    public OqlPathExpression Path => Paths[0];

    /// <summary>Gets the ordered key paths.</summary>
    public IReadOnlyList<OqlPathExpression> Paths { get; }

    /// <summary>Gets whether valued scalar keys must be unique.</summary>
    public bool IsUnique { get; }

    /// <summary>Gets whether an existing name is a no-op.</summary>
    public bool IfNotExists { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlCreateIndexExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlCreateIndexExpression value)
        {
            _target = value;
        }

        public string IndexName => _target.IndexName;
        public string Collection => _target.Collection;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlPathExpression> Paths => _target.Paths;
        public bool IsUnique => _target.IsUnique;
        public bool IfNotExists => _target.IfNotExists;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
