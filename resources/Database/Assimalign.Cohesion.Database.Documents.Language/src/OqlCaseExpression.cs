using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A searched or simple CASE expression, evaluated in branch order.</summary>
public sealed class OqlCaseExpression : OqlExpression
{
    /// <summary>Initializes a CASE expression.</summary>
    /// <param name="operand">The simple CASE operand, or null for searched CASE.</param>
    /// <param name="branches">Ordered WHEN keys and THEN values.</param>
    /// <param name="otherwise">The ELSE expression, or null for an implicit NULL result.</param>
    /// <param name="location">The source span.</param>
    public OqlCaseExpression(OqlExpression? operand, IReadOnlyList<KeyValuePair<OqlExpression, OqlExpression>> branches, OqlExpression? otherwise = null, Location? location = null) : base(location)
    {
        Operand = operand;
        Branches = branches;
        Otherwise = otherwise;
    }

    /// <summary>Gets the simple CASE operand, or null for searched CASE.</summary>
    public OqlExpression? Operand { get; }
    /// <summary>Gets the ordered WHEN keys and THEN values.</summary>
    public IReadOnlyList<KeyValuePair<OqlExpression, OqlExpression>> Branches { get; }
    /// <summary>Gets the ELSE expression, or null for implicit NULL.</summary>
    public OqlExpression? Otherwise { get; }
}
