using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A <c>DROP COLLECTION [IF EXISTS]</c> statement.</summary>
public sealed class OqlDropCollectionExpression : OqlExpression
{
    /// <summary>Initializes a collection removal statement.</summary>
    /// <param name="name">The database-local collection name.</param>
    /// <param name="ifExists">Whether an absent collection makes the statement a no-op.</param>
    /// <param name="location">The source span, with an exclusive end offset.</param>
    public OqlDropCollectionExpression(string name, bool ifExists = false, Location? location = null) : base(location)
    {
        Name = name;
        IfExists = ifExists;
    }

    /// <summary>Gets the database-local collection name.</summary>
    public string Name { get; }

    /// <summary>Gets whether an absent collection makes the statement a no-op.</summary>
    public bool IfExists { get; }
}
