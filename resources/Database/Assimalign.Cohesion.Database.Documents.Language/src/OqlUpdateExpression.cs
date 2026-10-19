using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Partially updates documents in one collection using SET and UNSET paths.</summary>
public sealed class OqlUpdateExpression : OqlExpression
{
    /// <summary>Initializes an UPDATE statement.</summary>
    /// <param name="collection">The target collection.</param>
    /// <param name="alias">The optional source alias.</param>
    /// <param name="assignments">The SET assignments in source order.</param>
    /// <param name="unset">The UNSET paths in source order.</param>
    /// <param name="predicate">The filter, or null to update every document.</param>
    /// <param name="location">The source span.</param>
    public OqlUpdateExpression(string collection, string? alias, IReadOnlyList<OqlUpdateAssignment> assignments,
        IReadOnlyList<OqlPathExpression> unset, OqlExpression? predicate, Location? location = null) : base(location)
    {
        Collection = collection;
        Alias = alias;
        Assignments = assignments;
        Unset = unset;
        Predicate = predicate;
    }

    /// <summary>Gets the target collection.</summary>
    public string Collection { get; }
    /// <summary>Gets the optional source alias.</summary>
    public string? Alias { get; }
    /// <summary>Gets the SET assignments in source order.</summary>
    public IReadOnlyList<OqlUpdateAssignment> Assignments { get; }
    /// <summary>Gets the UNSET paths in source order.</summary>
    public IReadOnlyList<OqlPathExpression> Unset { get; }
    /// <summary>Gets the filter, or null to update every document.</summary>
    public OqlExpression? Predicate { get; }
}
