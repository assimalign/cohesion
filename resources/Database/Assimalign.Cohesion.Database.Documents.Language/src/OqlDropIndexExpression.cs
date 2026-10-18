using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// A <c>DROP INDEX</c> statement that removes an index from a collection.
/// </summary>
public sealed class OqlDropIndexExpression : OqlExpression
{
    /// <summary>Initializes a document index removal statement.</summary>
    /// <param name="indexName">The name of the index to remove.</param>
    /// <param name="collection">The collection name; the executor rejects reserved system collections as read-only.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    public OqlDropIndexExpression(string indexName, string collection, Location? location = null)
        : this(indexName, collection, false, location) { }

    /// <summary>Initializes a conditional index removal statement.</summary>
    /// <param name="indexName">The index name.</param>
    /// <param name="collection">The collection name.</param>
    /// <param name="ifExists">Whether an absent index is a no-op.</param>
    /// <param name="location">The statement span.</param>
    public OqlDropIndexExpression(string indexName, string collection, bool ifExists, Location? location = null) : base(location)
    {
        IfExists = ifExists;
        IndexName = indexName;
        Collection = collection;
    }

    /// <summary>Gets the name of the index to remove.</summary>
    public string IndexName { get; }

    /// <summary>Gets the collection name; database qualification is never accepted.</summary>
    public string Collection { get; }

    /// <summary>Gets whether an absent index is a no-op.</summary>
    public bool IfExists { get; }
}
