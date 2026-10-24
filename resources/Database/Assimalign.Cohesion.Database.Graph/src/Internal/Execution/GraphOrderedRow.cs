using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One materialized projection and its pre-evaluated ordering keys.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphOrderedRow")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphOrderedRow.DebugView))]
internal sealed record GraphOrderedRow(object?[] Values, object?[] Keys, long Ordinal)
{
    internal IReadOnlyDictionary<string, object?>? Scope { get; init; }
    internal long RetainedBytes { get; set; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphOrderedRow _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphOrderedRow value)
        {
            _target = value;
        }

        public object?[] Values => _target.Values;
        public object?[] Keys => _target.Keys;
        public long Ordinal => _target.Ordinal;
    }
}
