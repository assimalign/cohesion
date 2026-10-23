using System;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One lexical ownership interval; a mutable local makes repeated disposal idempotent.</summary>
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
}
