using System;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One lexical ownership interval; a mutable local makes repeated disposal idempotent.</summary>
internal struct GraphMemoryLease(GraphQueryMemory owner, long bytes) : IDisposable
{
    private GraphQueryMemory? _owner = owner;

    public void Dispose()
    {
        var memory = _owner;
        _owner = null;
        memory?.EndLease(bytes);
    }
}
