using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlSequenceUsage")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlSequenceUsage
{
    internal static readonly object LastValue = new();
    private List<object?>? _names;
    private ulong _identity;
    internal List<object?> Names
    {
        get
        {
            if (_names is null)
            {
                _names = new();
                if (_identity != 0) { _names.Add(_identity); }
            }
            return _names;
        }
    }
    internal void AddIdentity(ulong id)
    {
        if (_identity == 0 && _names is null) { _identity = id; }
        else if (_names is null && _identity == id) { return; }
        else { Names.Add(id); }
    }
    internal bool TrySingleIdentity(out ulong id)
    {
        if (_names is null) { id = _identity; return id != 0; }
        if (_names.Count == 1 && _names[0] is ulong single) { id = single; return true; }
        id = 0; return false;
    }
    internal bool DynamicNames { get; set; }
    internal bool ContainsLike { get; set; }
    internal SqlExpressionEvaluator? Evaluator { get; set; }

    private sealed class DebugView
    { public DebugView(SqlSequenceUsage usage) { _ = usage; } }
}
