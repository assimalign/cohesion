using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Reuse the statement's existing function-binding slot. Statements without subqueries retain
// their original plan, planner, operation and iterator object sizes and allocate no registry.
[DebuggerDisplay("GraphCorrelatedBindings")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GraphCorrelatedBindings : IReadOnlyDictionary<GqlNamedFunctionCallExpression, GraphFunction>
{
    private readonly IReadOnlyDictionary<GqlNamedFunctionCallExpression, GraphFunction> _functions;
    internal GraphCorrelatedBindings(IReadOnlyDictionary<GqlNamedFunctionCallExpression, GraphFunction> functions,
        IReadOnlyDictionary<GqlExpression, GraphCorrelatedSubplan> subplans, IReadOnlySet<GqlExpression>? anti)
    { _functions = functions; Subplans = subplans; Anti = anti; }

    internal IReadOnlyDictionary<GqlExpression, GraphCorrelatedSubplan> Subplans { get; }
    internal IReadOnlySet<GqlExpression>? Anti { get; }
    internal System.Action? OnInnerAdvance { get; set; }
    internal System.Action? OnInnerDisposed { get; set; }
    public int Count => _functions.Count;
    public IEnumerable<GqlNamedFunctionCallExpression> Keys => _functions.Keys;
    public IEnumerable<GraphFunction> Values => _functions.Values;
    public GraphFunction this[GqlNamedFunctionCallExpression key] => _functions[key];
    public bool ContainsKey(GqlNamedFunctionCallExpression key) => _functions.ContainsKey(key);
    public bool TryGetValue(GqlNamedFunctionCallExpression key, out GraphFunction value) => _functions.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<GqlNamedFunctionCallExpression, GraphFunction>> GetEnumerator() => _functions.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class DebugView { public DebugView(GraphCorrelatedBindings value) { } }
}
