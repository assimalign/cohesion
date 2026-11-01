using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Indexes the grouped scope once for every RETURN and ORDER BY expression.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphGroupingScope")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphGroupingScope.DebugView))]
internal sealed class GraphGroupingScope
{
    private readonly HashSet<GqlExpression> _keys;
    private readonly HashSet<GqlExpression> _aggregates;
    private readonly HashSet<string> _variables;
    private readonly CancellationToken _token;
    private readonly GraphFunctionCatalog? _functions;

    internal GraphGroupingScope(IReadOnlyList<GqlExpression> keys, IReadOnlyList<GqlExpression> aggregates,
        GraphExpressionShape shape, CancellationToken token, GraphFunctionCatalog? functions = null)
    {
        _keys = new HashSet<GqlExpression>(shape);
        _aggregates = new HashSet<GqlExpression>(shape);
        _variables = new HashSet<string>(StringComparer.Ordinal);
        _token = token; _functions = functions;
        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();
            _keys.Add(key);
            if (key is GqlVariableExpression variable) { _variables.Add(variable.Variable); }
        }
        foreach (var aggregate in aggregates) { token.ThrowIfCancellationRequested(); _aggregates.Add(aggregate); }
    }

    internal void Validate(GqlExpression root, IReadOnlyDictionary<string, int>? aliases = null,
        IReadOnlySet<GqlExpression>? reusable = null, IReadOnlySet<string>? iterators = null,
        IReadOnlyDictionary<GqlExpression, GraphCorrelatedSubplan>? subplans = null)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        var pending = new Stack<GqlExpression>();
        pending.Push(root);
        while (pending.TryPop(out var expression))
        {
            _token.ThrowIfCancellationRequested();
            if ((iterators is null || !GraphValueExpressions.ReferencesAny(expression, iterators, _token)) &&
                (_keys.Contains(expression) && (reusable is null || reusable.Contains(expression)) || _aggregates.Contains(expression))) { continue; }
            if (expression is GqlExistsExpression or GqlPatternComprehensionExpression)
            {
                if (subplans is null || !subplans.TryGetValue(expression, out var subplan))
                { throw new GraphPlanningException("CG05027", ErrorMessages.CorrelatedPlanRequired, expression.Location); }
                foreach (string correlated in subplan.CorrelatedVariables)
                {
                    if (iterators?.Contains(correlated) == true || _variables.Contains(correlated) || aliases?.ContainsKey(correlated) == true) { continue; }
                    throw new GraphPlanningException("CG05001", ErrorMessages.GroupedExpressionRequired, expression.Location);
                }
                continue;
            }
            if (expression is GqlReduceExpression reduce)
            {
                Validate(reduce.List, aliases, reusable, iterators, subplans);
                Validate(reduce.Initial, aliases, reusable, iterators, subplans);
                var inner = iterators is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(iterators, StringComparer.Ordinal);
                inner.Add(reduce.Variable); inner.Add(reduce.Accumulator);
                Validate(reduce.Body, aliases, reusable, inner, subplans);
                continue;
            }
            if (expression is GqlListPredicateExpression predicate)
            {
                Validate(predicate.List, aliases, reusable, iterators, subplans);
                var inner = iterators is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(iterators, StringComparer.Ordinal);
                inner.Add(predicate.Variable);
                Validate(predicate.Predicate, aliases, reusable, inner, subplans);
                continue;
            }
            if (expression is GqlListComprehensionExpression comprehension)
            {
                Validate(comprehension.List, aliases, reusable, iterators, subplans);
                var inner = iterators is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(iterators, StringComparer.Ordinal);
                inner.Add(comprehension.Variable);
                if (comprehension.Predicate is not null) { Validate(comprehension.Predicate, aliases, reusable, inner, subplans); }
                if (comprehension.Projection is not null) { Validate(comprehension.Projection, aliases, reusable, inner, subplans); }
                continue;
            }
            string? name = expression switch
            {
                GqlVariableExpression variable => variable.Variable,
                GqlPropertyExpression property => property.Variable,
                GqlLabeledPredicate labeled => labeled.Variable,
                _ => null,
            };
            if (name is not null && (iterators?.Contains(name) == true || _variables.Contains(name) || aliases is not null && aliases.ContainsKey(name))) { continue; }
            if (name is not null || expression is GqlAggregateExpression || expression is GqlNamedFunctionCallExpression named && _functions?.Resolve(named) is GraphAggregateFunction)
            { throw new GraphPlanningException("CG05001", ErrorMessages.GroupedExpressionRequired, expression.Location); }
            foreach (var child in GraphValueExpressions.Children(expression)) { pending.Push(child); }
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphGroupingScope value)
        {
            _ = value;
        }
    }
}
