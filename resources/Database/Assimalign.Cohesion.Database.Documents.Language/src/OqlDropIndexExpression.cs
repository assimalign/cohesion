using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// A <c>DROP INDEX</c> statement that removes an index from a collection.
/// </summary>
public sealed class OqlDropIndexExpression : OqlExpression
{
    /// <summary>Initializes a document index removal statement.</summary>
    /// <param name="indexName">The name of the index to remove.</param>
    /// <param name="collection">The unqualified collection name.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    public OqlDropIndexExpression(string indexName, string collection, Location? location = null) : base(location)
    {
        IndexName = indexName;
        Collection = collection;
    }

    /// <summary>Gets the name of the index to remove.</summary>
    public string IndexName { get; }

    /// <summary>Gets the unqualified collection name.</summary>
    public string Collection { get; }
}
