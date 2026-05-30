namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Determines how an enumerable target member is populated when mapping.
/// </summary>
public enum MapperCollectionHandling
{
    /// <summary>
    /// Replaces the target collection with the mapped source elements.
    /// </summary>
    Override = 0,

    /// <summary>
    /// Preserves the existing target elements and appends the mapped source elements.
    /// </summary>
    Merge = 1
}
