using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Builder-owned registrations. Native names and grammar forms are reserved.</summary>
[System.Diagnostics.DebuggerDisplay("GraphFunctionCollection {Count}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphFunctionCollection.DebugView))]
public sealed class GraphFunctionCollection : IReadOnlyCollection<GraphFunction>
{
    private readonly List<GraphFunction> _functions = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action? _ensureMutable;
    private bool _frozen;
    internal GraphFunctionCollection(Action? ensureMutable = null)
    {
        _ensureMutable = ensureMutable;
        foreach (var identity in Enum.GetValues<GqlScalarFunction>()) { AddNative(new GraphNativeScalarFunction(identity)); }
        foreach (var identity in Enum.GetValues<GqlAggregateFunction>()) { AddNative(new GraphNativeAggregateFunction(identity)); }
        foreach (string keyword in GqlLanguageProfile.Instance.Keywords) { _names.Add(keyword); }
        foreach (string function in GqlLanguageProfile.Instance.Functions) { _names.Add(function); }
    }
    private void AddNative(GraphFunction function) { _functions.Add(function); _names.Add(function.Name.ToString()); }
    /// <summary>Gets the registration count, including built-ins.</summary>
    public int Count => _functions.Count;
    /// <summary>Registers one scalar or aggregate before the build begins.</summary><param name="function">The immutable function.</param>
    /// <returns>This collection for chaining registrations.</returns>
    /// <exception cref="ArgumentNullException">The function is null.</exception>
    /// <exception cref="ArgumentException">The name is reserved.</exception>
    /// <exception cref="InvalidOperationException">The builder has begun construction, or the signature is already registered.</exception>
    public GraphFunctionCollection Add(GraphFunction function)
    {
        ArgumentNullException.ThrowIfNull(function); _ensureMutable?.Invoke();
        if (_frozen) { throw new InvalidOperationException(ErrorMessages.FunctionCatalogFrozen); }
        if (_functions.Any(existing => !existing.IsNative && existing.Name == function.Name &&
            ((existing is GraphAggregateFunction) != (function is GraphAggregateFunction) || existing.Parameters.Count == function.Parameters.Count)))
        { throw new InvalidOperationException(ErrorMessages.FunctionSignatureDuplicate); }
        // Probe only grammar admission, never a callback or an engine: recognized unsupported
        // words outside the lexical keyword table must not become uncallable registrations.
        var admission = new GqlQueryParser(allowCustomFunctions: true).Parse("RETURN " + function.Name + "()");
        if (admission.Diagnostics.Any(diagnostic => diagnostic.Severity == Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error) ||
            _names.Contains(function.Name.ToString()))
        { throw new ArgumentException(ErrorMessages.FunctionNameReserved, nameof(function)); }
        _functions.Add(function);
        return this;
    }
    internal GraphFunctionCatalog Freeze() { _frozen = true; return new(_functions.ToArray()); }
    /// <summary>Enumerates registered function metadata.</summary><returns>The enumerator.</returns>
    public IEnumerator<GraphFunction> GetEnumerator() => _functions.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private sealed class DebugView
    {
        private readonly GraphFunctionCollection _target;
        public DebugView(GraphFunctionCollection value) { _target = value; }
        public int Count => _target.Count;
    }
}
