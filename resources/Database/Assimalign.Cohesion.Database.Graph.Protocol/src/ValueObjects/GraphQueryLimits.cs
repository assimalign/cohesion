namespace Assimalign.Cohesion.Database.Graph.Protocol;

/// <summary>Optional lower query limits; omitted members use the engine's configured ceilings.</summary>
/// <remarks>Limits apply to one statement. A request cannot exceed its engine's ceilings.</remarks>
public sealed class GraphQueryLimits
{
    /// <summary>Gets the maximum accounted retained execution memory in bytes, or null for the engine ceiling.</summary>
    public long? MemoryBytes { get; init; }

    /// <summary>Gets the maximum physical work units, or null for the engine ceiling.</summary>
    /// <remarks>Graph candidates are charged after direction/type selection, including dead ends and merge heads.</remarks>
    public long? WorkUnits { get; init; }
}
