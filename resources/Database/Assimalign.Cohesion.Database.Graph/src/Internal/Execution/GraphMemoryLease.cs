using System;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One lexical ownership interval; a mutable local makes repeated disposal idempotent.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphMemoryLease")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphMemoryLease.DebugView))]
internal struct GraphMemoryLease : IDisposable
{
    private GraphQueryMemory? _owner;
    private readonly long _bytes;

    internal GraphMemoryLease(GraphQueryMemory owner, long bytes)
    {
        _owner = owner;
        _bytes = bytes;
    }

    public void Dispose()
    {
        var memory = _owner;
        _owner = null;
        memory?.EndLease(_bytes);
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphMemoryLease value)
        {
            _ = value;
        }
    }
}
