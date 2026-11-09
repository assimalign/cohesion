using System;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A MERGE source; a subquery requires an alias, while a collection or path can supply an implicit name.</summary>
public sealed record OqlMergeSource
{
    /// <summary>Initializes a MERGE source.</summary>
    /// <param name="collection">The keyspace name; empty for an expression source.</param>
    /// <param name="alias">The explicit alias; null uses the collection or final path member.</param>
    /// <param name="expression">The source expression, including a subquery; null selects the collection.</param>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    public OqlMergeSource(string collection, string? alias, OqlExpression? expression = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        Collection = collection; Alias = alias; Expression = expression;
    }

    /// <summary>Gets the keyspace name; empty for an expression source.</summary>
    public string Collection { get; }
    /// <summary>Gets the explicit alias.</summary>
    public string? Alias { get; }
    /// <summary>Gets the optional source expression.</summary>
    public OqlExpression? Expression { get; }
}
