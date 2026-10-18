using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>A group exposes computed keys and aggregates, never an arbitrary input row.</summary>
internal sealed class GraphGroupedBinding : Dictionary<string, object?>
{
    internal GraphGroupedBinding(IReadOnlyDictionary<GqlExpression, object?> valuesByExpression)
        : base(StringComparer.Ordinal)
    {
        ValuesByExpression = valuesByExpression;
        foreach (var pair in valuesByExpression)
        { if (pair.Key is GqlVariableExpression variable) { Add(variable.Variable, pair.Value); } }
    }

    internal IReadOnlyDictionary<GqlExpression, object?> ValuesByExpression { get; }
}
