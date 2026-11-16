using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A query block or a compound query, with ordering and paging in its own scope.</summary>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class OqlQueryExpression : OqlExpression
{
    private protected OqlQueryExpression(IReadOnlyList<OqlOrdering> orderBy, bool isValue,
        OqlExpression? limit, OqlExpression? offset, Location? location) : base(location)
    {
        OrderBy = orderBy;
        IsValue = isValue;
        Limit = limit;
        Offset = offset;
    }

    /// <summary>Gets the ordering expressions in this query scope.</summary>
    public IReadOnlyList<OqlOrdering> OrderBy { get; }
    /// <summary>Gets whether every query block returns one RAW or VALUE expression.</summary>
    public bool IsValue { get; }
    /// <summary>Gets the optional maximum row count in this query scope.</summary>
    public OqlExpression? Limit { get; }
    /// <summary>Gets the optional number of rows to skip in this query scope.</summary>
    public OqlExpression? Offset { get; }
}
