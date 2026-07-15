namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Conditions for a key-value write. The two conditions are mutually exclusive —
/// one expects the key to be absent, the other expects a specific current entry.
/// </summary>
public sealed class KeyValuePutOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the write applies only when the
    /// key has no visible entry (insert-only).
    /// </summary>
    public bool OnlyIfAbsent { get; set; }

    /// <summary>
    /// Gets or sets the etag the key's current entry must carry for the write to
    /// apply (compare-and-swap), or null for an unconditional write.
    /// </summary>
    public long? ExpectedETag { get; set; }
}
