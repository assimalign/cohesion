using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Statement-local name slots shared by streaming rows; slots never change identity.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphBindingLayout")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphBindingLayout.DebugView))]
internal sealed class GraphBindingLayout
{
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];
    private readonly GraphMemoryOwner? _memory;

    internal GraphBindingLayout(GraphMemoryOwner? memory = null) => _memory = memory;

    internal int Count => _names.Count;
    internal string Name(int slot) => _names[slot];
    internal bool TryGetSlot(string name, out int slot) => _slots.TryGetValue(name, out slot);

    internal int GetSlot(string name)
    {
        if (_slots.TryGetValue(name, out int slot)) { return slot; }
        _memory?.Add(80);
        slot = _names.Count;
        _slots.Add(name, slot);
        _names.Add(name);
        return slot;
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphBindingLayout value)
        {
            _ = value;
        }
    }
}
