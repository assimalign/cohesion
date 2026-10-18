using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Indexes the grouped scope once for every RETURN and ORDER BY expression.</summary>
internal sealed class GraphGroupingScope
{
    private readonly HashSet<GqlExpression> _keys;
    private readonly HashSet<GqlExpression> _aggregates;
    private readonly HashSet<string> _variables;
    private readonly CancellationToken _token;

    internal GraphGroupingScope(IReadOnlyList<GqlExpression> keys, IReadOnlyList<GqlAggregateExpression> aggregates,
        GraphExpressionShape shape, CancellationToken token)
    {
        _keys = new HashSet<GqlExpression>(shape);
        _aggregates = new HashSet<GqlExpression>(shape);
        _variables = new HashSet<string>(StringComparer.Ordinal);
        _token = token;
        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();
            _keys.Add(key);
            if (key is GqlVariableExpression variable) { _variables.Add(variable.Variable); }
        }
        foreach (var aggregate in aggregates) { token.ThrowIfCancellationRequested(); _aggregates.Add(aggregate); }
    }

    internal void Validate(GqlExpression root, IReadOnlyDictionary<string, int>? aliases = null,
        IReadOnlySet<GqlExpression>? reusable = null)
    {
        var pending = new Stack<GqlExpression>();
        pending.Push(root);
        while (pending.TryPop(out var expression))
        {
            _token.ThrowIfCancellationRequested();
            if (_keys.Contains(expression) && (reusable is null || reusable.Contains(expression)) || _aggregates.Contains(expression)) { continue; }
            string? name = expression switch
            {
                GqlVariableExpression variable => variable.Variable,
                GqlPropertyExpression property => property.Variable,
                GqlLabeledPredicate labeled => labeled.Variable,
                _ => null,
            };
            if (name is not null && (_variables.Contains(name) || aliases is not null && aliases.ContainsKey(name))) { continue; }
            if (name is not null || expression is GqlAggregateExpression)
            { throw new GraphPlanningException("COHDBG001", ErrorMessages.GroupedExpressionRequired, expression.Location); }
            foreach (var child in GraphValueExpressions.Children(expression)) { pending.Push(child); }
        }
    }
}
