using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>A group exposes computed keys and aggregates, never an arbitrary input row.</summary>
internal sealed class GraphGroupedBinding : Dictionary<string, object?>
{
    private readonly IReadOnlySet<GqlExpression>? _reusable;

    internal GraphGroupedBinding(IReadOnlyDictionary<GqlExpression, object?> valuesByExpression,
        IReadOnlySet<GqlExpression>? reusable = null)
        : base(StringComparer.Ordinal)
    {
        ValuesByExpression = valuesByExpression;
        _reusable = reusable;
        foreach (var pair in valuesByExpression)
        { if (pair.Key is GqlVariableExpression variable) { Add(variable.Variable, pair.Value); } }
    }

    internal IReadOnlyDictionary<GqlExpression, object?> ValuesByExpression { get; }

    internal bool TryGetComputed(GqlExpression expression, out object? value)
    {
        if (_reusable is null || _reusable.Contains(expression)) { return ValuesByExpression.TryGetValue(expression, out value); }
        value = null;
        return false;
    }
}
