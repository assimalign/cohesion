namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Graph's synthetic object ids, above the signed sequence allocator's range.</summary>
internal static class GraphLockNamespaces
{
    internal const ulong Adjacency = ulong.MaxValue;
    internal const ulong Labels = ulong.MaxValue - 1;
    internal const ulong RelationshipTypes = ulong.MaxValue - 2;
    // Logical locks only: never a persisted index or an allocated catalog identity.
    internal const ulong MergePatterns = ulong.MaxValue - 3;
}
