using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Cleared identity-accounting storage; never contains a live value when cached.</summary>
internal sealed class GraphQueryMemoryBuffers
{
    internal Dictionary<object, (int References, long Bytes)> Values { get; } = new(ReferenceEqualityComparer.Instance);
    internal Stack<object?> Pending { get; } = new();
    internal List<object> Journal { get; } = [];
}
