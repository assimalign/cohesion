using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A bracketed list of expressions, supported only as the right operand of IN.</summary>
public sealed class GqlListExpression : GqlExpression
{
    /// <summary>Initializes a list over a copy of its items.</summary>
    /// <param name="items">The items in source order.</param>
    /// <param name="location">The source span.</param>
    public GqlListExpression(IEnumerable<GqlExpression> items, Location? location = null) : base(location)
    {
        Items = items is null ? null! : new List<GqlExpression>(items).AsReadOnly();
    }

    /// <summary>Gets the items in source order.</summary>
    public IReadOnlyList<GqlExpression> Items { get; }
}
