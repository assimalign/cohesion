using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Owned slot values, with absence distinct from an OPTIONAL MATCH null binding.</summary>
internal sealed class GraphBindingRow : IReadOnlyDictionary<string, object?>
{
    private static readonly object _unbound = new();
    private readonly GraphBindingLayout _layout;
    private object?[] _values;

    internal GraphBindingRow(GraphBindingLayout layout)
    {
        _layout = layout;
        _values = new object?[layout.Count];
        Array.Fill(_values, _unbound);
    }

    internal GraphBindingRow(GraphBindingLayout layout, IReadOnlyDictionary<string, object?> source)
        : this(layout)
    {
        Reset(source);
    }

    private GraphBindingRow(GraphBindingRow source)
    {
        _layout = source._layout;
        _values = (object?[])source._values.Clone();
        Count = source.Count;
    }

    internal GraphBindingRow Fork() => new(this);

    internal void Reset(IReadOnlyDictionary<string, object?> source)
    {
        if (ReferenceEquals(this, source)) { return; }
        Array.Fill(_values, _unbound);
        Count = 0;
        if (source is GraphBindingRow row)
        {
            for (int i = 0; i < row._values.Length; i++)
            { if (!ReferenceEquals(row._values[i], _unbound)) { Add(row._layout.Name(i), row._values[i]); } }
        }
        else if (source is Dictionary<string, object?> dictionary)
        { foreach (var pair in dictionary) { Add(pair.Key, pair.Value); } }
        else { foreach (var pair in source) { Add(pair.Key, pair.Value); } }
    }

    // Conservative retained container accounting on both 32- and 64-bit runtimes.
    internal long RetainedBytes => 64L + (long)IntPtr.Size * _values.Length;

    public int Count { get; private set; }
    public IEnumerable<string> Keys
    {
        get { foreach (var pair in this) { yield return pair.Key; } }
    }
    public IEnumerable<object?> Values
    {
        get { foreach (var pair in this) { yield return pair.Value; } }
    }
    public object? this[string name]
    {
        get => TryGetValue(name, out var value) ? value : throw new KeyNotFoundException();
        set
        {
            int slot = _layout.GetSlot(name);
            EnsureSlot(slot);
            if (ReferenceEquals(_values[slot], _unbound)) { Count++; }
            _values[slot] = value;
        }
    }

    internal void Add(string name, object? value)
    {
        if (!TryAdd(name, value)) { throw new ArgumentException(); }
    }

    internal bool TryAdd(string name, object? value)
    {
        if (ContainsKey(name)) { return false; }
        this[name] = value;
        return true;
    }

    internal bool Remove(string name)
    {
        if (!_layout.TryGetSlot(name, out int slot) || slot >= _values.Length || ReferenceEquals(_values[slot], _unbound))
        { return false; }
        _values[slot] = _unbound;
        Count--;
        return true;
    }

    public bool ContainsKey(string name) => TryGetValue(name, out _);

    public bool TryGetValue(string name, out object? value)
    {
        if (_layout.TryGetSlot(name, out int slot) && slot < _values.Length && !ReferenceEquals(_values[slot], _unbound))
        { value = _values[slot]; return true; }
        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        int count = Math.Min(_values.Length, _layout.Count);
        for (int i = 0; i < count; i++)
        {
            if (!ReferenceEquals(_values[i], _unbound)) { yield return new(_layout.Name(i), _values[i]); }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureSlot(int slot)
    {
        if (slot < _values.Length) { return; }
        int previous = _values.Length;
        Array.Resize(ref _values, Math.Max(slot + 1, Math.Max(4, previous * 2)));
        Array.Fill(_values, _unbound, previous, _values.Length - previous);
    }
}
