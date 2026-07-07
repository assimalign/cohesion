namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// A half-open or closed range over the index key space, used for range scans.
/// </summary>
/// <param name="Start">The lower bound, or null to scan from the first key.</param>
/// <param name="End">The upper bound, or null to scan to the last key.</param>
/// <param name="IsStartInclusive">Whether <paramref name="Start"/> itself is included.</param>
/// <param name="IsEndInclusive">Whether <paramref name="End"/> itself is included.</param>
public readonly record struct IndexKeyRange(
    IndexKey? Start,
    IndexKey? End,
    bool IsStartInclusive = true,
    bool IsEndInclusive = false)
{
    /// <summary>
    /// Gets the unbounded range covering the whole key space.
    /// </summary>
    public static IndexKeyRange All => new(null, null);
}
