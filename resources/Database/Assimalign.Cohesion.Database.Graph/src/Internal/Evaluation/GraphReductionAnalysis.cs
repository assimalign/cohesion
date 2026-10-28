using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// One immutable incoming scope's analyses share closures across requested depths. A body
// observes changing accumulator facts, so nested closures inside it must be recomputed.
[System.Diagnostics.DebuggerDisplay("GraphReductionAnalysis")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphReductionAnalysis.DebugView))]
internal sealed class GraphReductionAnalysis
{
    private readonly GraphReductionWork _work;
    private readonly Dictionary<GqlReduceExpression, Dictionary<(int Mode, Delegate Scope), object>> _closures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GqlExpression, (bool Flat, bool Scalars)> _lists = new(ReferenceEqualityComparer.Instance);
    private int _bodyDepth;

    internal GraphReductionAnalysis(GraphReductionWork work) { _work = work; }
    internal void Visit(GqlExpression expression) => _work.Visit(expression);
    internal bool ScalarList(GqlExpression expression, IReadOnlyDictionary<string, object?>? parameters, out bool scalars)
    {
        scalars = false;
        if (expression is not (GqlListExpression or GqlParameterExpression)) { return false; }
        if (expression is GqlParameterExpression && parameters is null) { return false; }
        if (!_lists.TryGetValue(expression, out var result))
        {
            result = (true, true);
            if (expression is GqlListExpression list)
            {
                result.Scalars = list.Items.Count > 0;
                foreach (var item in list.Items)
                {
                    Visit(item);
                    if (item is not GqlLiteralExpression literal || !FlatValue(literal.Value)) { result = default; break; }
                    result.Scalars &= literal.Value is not null;
                }
            }
            else if (expression is GqlParameterExpression parameter && parameters?.TryGetValue(parameter.Name, out var binding) == true && binding is object?[] values)
            {
                result.Scalars = values.Length > 0;
                foreach (var value in values)
                {
                    Visit(expression);
                    if (!FlatValue(value)) { result = default; break; }
                    result.Scalars &= value is not null;
                }
            }
            else { result = default; }
            _lists.Add(expression, result);
        }
        scalars = result.Scalars;
        return result.Flat;
    }

    private static bool FlatValue(object? value) => value is null or bool or string or long or int or short or sbyte or double or float or decimal or DateOnly or DateTimeOffset;

    internal void EnterBody() => _bodyDepth++;
    internal void ExitBody() => _bodyDepth--;

    internal GraphReductionClosure<T> Closure<T>(GqlReduceExpression expression, int mode, Func<string, int, T> scope,
        Func<GqlExpression, Func<string, int, T>, int, T> analyze, Func<T, T, T> merge, T horizon, Delegate? identity = null)
    {
        if (_bodyDepth > 0) { return new(expression, scope, analyze, merge, horizon, this); }
        if (!_closures.TryGetValue(expression, out var scopes))
        { scopes = new(); _closures.Add(expression, scopes); }
        var key = (mode, identity ?? scope);
        if (!scopes.TryGetValue(key, out var closure))
        { closure = new GraphReductionClosure<T>(expression, scope, analyze, merge, horizon, this); scopes.Add(key, closure); }
        return (GraphReductionClosure<T>)closure;
    }

    private sealed class DebugView
    {
        public DebugView(GraphReductionAnalysis value) { }
    }
}

[System.Diagnostics.DebuggerDisplay("GraphReductionClosure")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphReductionClosure<>.DebugView))]
internal sealed class GraphReductionClosure<T>
{
    private const int count = GraphValueCodec.MaxDepth + 2;
    private readonly GqlReduceExpression _expression;
    private readonly Func<string, int, T> _outer;
    private readonly Func<GqlExpression, Func<string, int, T>, int, T> _analyze;
    private readonly Func<T, T, T> _merge;
    private readonly T _horizon;
    private readonly GraphReductionAnalysis _analysis;
    private readonly T[] _values = new T[count];
    private readonly T[] _elements = new T[count];
    private readonly bool[] _demanded = new bool[count];
    private readonly bool[] _elementKnown = new bool[count];
    private readonly bool[] _queued = new bool[count];
    private bool[,]? _dependents;
    private readonly Queue<int> _pending = new();
    private readonly Func<string, int, T> _inner;
    private int _current = -1;

    internal GraphReductionClosure(GqlReduceExpression expression, Func<string, int, T> outer,
        Func<GqlExpression, Func<string, int, T>, int, T> analyze, Func<T, T, T> merge, T horizon, GraphReductionAnalysis analysis)
    {
        _expression = expression; _outer = outer; _analyze = analyze; _merge = merge;
        _horizon = horizon; _analysis = analysis; _inner = Inner;
    }

    internal T At(int depth)
    {
        if (depth >= count) { return _horizon; }
        _ = Demand(depth);
        while (_pending.TryDequeue(out int access))
        {
            _analysis.Visit(_expression);
            _queued[access] = false;
            _current = access;
            T body;
            _analysis.EnterBody();
            try { body = _analyze(_expression.Body, _inner, access); }
            finally { _analysis.ExitBody(); _current = -1; }
            var next = _merge(_values[access], body);
            if (EqualityComparer<T>.Default.Equals(next, _values[access])) { continue; }
            _values[access] = next;
            if (_dependents is null) { continue; }
            for (int i = 0; i < count; i++)
            { if (_dependents[access, i]) { Enqueue(i); } }
        }
        return _values[depth];
    }

    private T Demand(int access)
    {
        if (access >= count) { return _horizon; }
        if (_current >= 0) { (_dependents ??= new bool[count, count])[access, _current] = true; }
        if (!_demanded[access])
        {
            _analysis.Visit(_expression.Initial);
            _values[access] = _analyze(_expression.Initial, _outer, access);
            _demanded[access] = true;
            Enqueue(access);
        }
        return _values[access];
    }

    private T Inner(string name, int access)
    {
        if (name == _expression.Accumulator) { return Demand(access); }
        if (name != _expression.Variable) { return _outer(name, access); }
        if (access >= count) { return _horizon; }
        if (!_elementKnown[access])
        {
            _analysis.Visit(_expression.List);
            _elements[access] = _analyze(_expression.List, _outer, access + 1);
            _elementKnown[access] = true;
        }
        return _elements[access];
    }

    private void Enqueue(int access)
    { if (!_queued[access]) { _queued[access] = true; _pending.Enqueue(access); } }

    private sealed class DebugView
    {
        public DebugView(GraphReductionClosure<T> value) { }
    }
}
