using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// A <c>CREATE INDEX</c> statement that creates an index over one document path.
/// </summary>
public sealed class OqlCreateIndexExpression : OqlExpression
{
    /// <summary>Initializes a document index creation statement.</summary>
    /// <param name="indexName">The name of the index to create.</param>
    /// <param name="collection">The unqualified collection name.</param>
    /// <param name="path">The document path indexed within the collection.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    public OqlCreateIndexExpression(
        string indexName,
        string collection,
        OqlPathExpression path,
        Location? location = null) : base(location)
    {
        IndexName = indexName;
        Collection = collection;
        Path = path;
    }

    /// <summary>Gets the name of the index to create.</summary>
    public string IndexName { get; }

    /// <summary>Gets the unqualified collection name.</summary>
    public string Collection { get; }

    /// <summary>Gets the document path indexed within the collection.</summary>
    public OqlPathExpression Path { get; }
}
