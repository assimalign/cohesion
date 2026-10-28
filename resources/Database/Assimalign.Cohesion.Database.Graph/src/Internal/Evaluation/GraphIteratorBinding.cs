using System.Collections;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>A lexical iterator frame; never mutates the enclosing row.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphIteratorBinding")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GraphIteratorBinding.DebugView))]
internal sealed class GraphIteratorBinding : IReadOnlyDictionary<string, object?>
{
    private readonly IReadOnlyDictionary<string, object?> _outer;
    private readonly string _name;
    private readonly HashSet<string> _names;

    internal GraphIteratorBinding(IReadOnlyDictionary<string, object?> outer, string name)
    {
        _outer = outer;
        _name = name;
        _names = new(System.StringComparer.Ordinal) { name };
    }

    internal object? Value { get; set; }
    internal bool TryGetComputed(GqlExpression expression, out object? value)
    {
        if (!GraphValueExpressions.ReferencesAny(expression, _names, default))
        {
            if (_outer is GraphGroupedBinding grouped) { return grouped.TryGetComputed(expression, out value); }
            if (_outer is GraphIteratorBinding iterator) { return iterator.TryGetComputed(expression, out value); }
        }
        value = null;
        return false;
    }
    public object? this[string key] => key == _name ? Value : _outer[key];
    public int Count => _outer.Count + (_outer.ContainsKey(_name) ? 0 : 1);
    public IEnumerable<string> Keys { get { foreach (var pair in this) { yield return pair.Key; } } }
    public IEnumerable<object?> Values { get { foreach (var pair in this) { yield return pair.Value; } } }
    public bool ContainsKey(string key) => key == _name || _outer.ContainsKey(key);
    public bool TryGetValue(string key, out object? value)
    {
        if (key == _name) { value = Value; return true; }
        return _outer.TryGetValue(key, out value);
    }
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var pair in _outer) { if (pair.Key != _name) { yield return pair; } }
        yield return new(_name, Value);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class DebugView
    {
        public DebugView(GraphIteratorBinding value) { _ = value; }
    }
}
