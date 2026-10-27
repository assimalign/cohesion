using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Cleared identity-accounting storage; never contains a live value when cached.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphQueryMemoryBuffers")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryMemoryBuffers.DebugView))]
internal sealed class GraphQueryMemoryBuffers
{
    internal Dictionary<object, (int References, long Bytes)> Values { get; } = new(ReferenceEqualityComparer.Instance);
    internal Stack<object?> Pending { get; } = new();
    internal List<object> Journal { get; } = [];

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryMemoryBuffers value)
        {
            _ = value;
        }
    }
}
