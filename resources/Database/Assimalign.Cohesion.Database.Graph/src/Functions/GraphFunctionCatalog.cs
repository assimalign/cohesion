using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>The immutable, engine-local catalog of native and developer functions.</summary>
[System.Diagnostics.DebuggerDisplay("GraphFunctionCatalog {Count}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphFunctionCatalog.DebugView))]
public sealed class GraphFunctionCatalog : IReadOnlyCollection<GraphFunction>
{
    private readonly GraphFunction[] _functions;
    private readonly FrozenDictionary<string, IReadOnlyList<GraphFunction>> _names;
    private readonly GraphScalarFunction[] _scalars;
    private readonly GraphAggregateFunction[] _aggregates;
    internal static GraphFunctionCatalog Standard { get; } = new(new GraphFunctionCollection().ToArray());
    internal GraphFunctionCatalog(GraphFunction[] functions)
    {
        _functions = functions;
        var names = functions.GroupBy(f => f.Name.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<GraphFunction>)Array.AsReadOnly(group.ToArray()), StringComparer.OrdinalIgnoreCase);
        foreach (var alias in new (string Name, string Canonical)[]
        {
            ("elementId", "element_id"), ("length", "path_length"), ("relationships", "edges"),
            ("toLower", "lower"), ("toUpper", "upper"), ("character_length", "char_length"),
            ("ceiling", "ceil"), ("ln", "log"), ("collect", "collect_list"),
        })
        { if (names.TryGetValue(alias.Canonical, out var entries)) { names[alias.Name] = entries; } }
        _names = names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _scalars = functions.OfType<GraphNativeScalarFunction>().OrderBy(f => f.Identity).Cast<GraphScalarFunction>().ToArray();
        _aggregates = functions.OfType<GraphNativeAggregateFunction>().OrderBy(f => f.Identity).Cast<GraphAggregateFunction>().ToArray();
    }
    /// <summary>Gets the number of registered identities.</summary>
    public int Count => _functions.Length;
    /// <summary>Finds the first registered overload by identifier.</summary><param name="name">The identifier.</param><returns>The function, or null if unregistered.</returns>
    /// <exception cref="ArgumentException">The name is default or invalid.</exception>
    public GraphFunction? Find(GraphFunctionName name)
    {
        if (!GraphFunctionName.IsValid(name.ToString(), out string? error)) { throw new ArgumentException(error, nameof(name)); }
        return GetOverloads(name).FirstOrDefault();
    }
    /// <summary>Gets every overload of a name, in registration order; never null.</summary>
    /// <param name="name">The identifier.</param><returns>Immutable overloads, or an empty list.</returns>
    /// <exception cref="ArgumentException">The name is default or invalid.</exception>
    public IReadOnlyList<GraphFunction> GetOverloads(GraphFunctionName name)
    {
        if (!GraphFunctionName.IsValid(name.ToString(), out string? error)) { throw new ArgumentException(error, nameof(name)); }
        return _names.GetValueOrDefault(name.ToString()) ?? Array.Empty<GraphFunction>();
    }
    internal GraphScalarFunction Native(GqlScalarFunction function) => _scalars[(int)function];
    internal GraphAggregateFunction Native(GqlAggregateFunction function) => _aggregates[(int)function];
    internal GraphFunction Resolve(GqlNamedFunctionCallExpression call)
    {
        var overloads = GetOverloads(call.Name);
        foreach (var function in overloads)
        { if (function.AcceptsArity(call.Arguments.Count)) { return function; } }
        throw new GraphPlanningException(overloads.Count == 0 ? "CG00010" : "CG05001",
            string.Format(System.Globalization.CultureInfo.CurrentCulture, overloads.Count == 0 ? ErrorMessages.FunctionUnknown : ErrorMessages.FunctionArity, call.Name), call.Location);
    }
    /// <summary>Enumerates immutable registration metadata.</summary><returns>The enumerator.</returns>
    public IEnumerator<GraphFunction> GetEnumerator() => ((IEnumerable<GraphFunction>)_functions).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private sealed class DebugView
    {
        private readonly GraphFunctionCatalog _target;
        public DebugView(GraphFunctionCatalog value) { _target = value; }
        public int Count => _target.Count;
    }
}
