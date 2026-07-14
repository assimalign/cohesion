namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// The condition of a conditional key-value write: insert-only
/// (<see cref="IfAbsent"/>) or compare-and-swap against a specific etag
/// (<see cref="IfETagMatches"/>).
/// </summary>
public readonly struct KeyValueWriteCondition
{
    private KeyValueWriteCondition(long? expectedETag, bool onlyIfAbsent)
    {
        ExpectedETag = expectedETag;
        OnlyIfAbsent = onlyIfAbsent;
    }

    /// <summary>
    /// Gets the etag the key's current entry must carry for the write to apply,
    /// or null when the condition is <see cref="OnlyIfAbsent"/>.
    /// </summary>
    public long? ExpectedETag { get; }

    /// <summary>
    /// Gets a value indicating whether the write applies only when the key has
    /// no visible entry.
    /// </summary>
    public bool OnlyIfAbsent { get; }

    /// <summary>
    /// Gets the insert-only condition: the write applies only when the key has
    /// no visible entry.
    /// </summary>
    public static KeyValueWriteCondition IfAbsent => new(null, onlyIfAbsent: true);

    /// <summary>
    /// Creates the compare-and-swap condition: the write applies only when the
    /// key's current entry carries the given etag.
    /// </summary>
    /// <param name="expectedETag">The etag the current entry must carry.</param>
    /// <returns>The condition.</returns>
    public static KeyValueWriteCondition IfETagMatches(long expectedETag) => new(expectedETag, onlyIfAbsent: false);
}
