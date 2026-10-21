using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

internal sealed class IncidentDisposalProbe : IAsyncEnumerator<StoredGraphRelationship>
{
    private readonly Exception? _failure;

    internal IncidentDisposalProbe(Exception? failure = null) { _failure = failure; }

    internal bool Disposed { get; private set; }
    public StoredGraphRelationship Current => default;
    public ValueTask<bool> MoveNextAsync() => new(false);
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return _failure is null ? default : ValueTask.FromException(_failure);
    }
}
