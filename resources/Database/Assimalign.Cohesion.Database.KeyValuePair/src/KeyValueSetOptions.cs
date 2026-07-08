using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Options for a key-value write.
/// </summary>
public sealed class KeyValueSetOptions
{
    /// <summary>
    /// Gets or sets the time-to-live for the entry. Null (the default) stores the
    /// entry without expiration.
    /// </summary>
    public TimeSpan? TimeToLive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the write succeeds only when the
    /// key does not already exist.
    /// </summary>
    public bool OnlyIfAbsent { get; set; }
}
