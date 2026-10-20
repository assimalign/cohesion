namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One materialized projection and its pre-evaluated ordering keys.</summary>
internal sealed record GraphOrderedRow(object?[] Values, object?[] Keys, int Ordinal);
