namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// The outcome of a key-value write: whether it applied, and the relevant etag.
/// A conditional miss (compare-and-swap mismatch, or insert-only against an
/// existing key) is this first-class outcome — never an exception. Concurrency
/// conflicts with other transactions are different: those surface as the root's
/// retryable transaction exceptions.
/// </summary>
/// <param name="Applied">Whether the write applied.</param>
/// <param name="ETag">
/// When <paramref name="Applied"/> is true, the entry's new etag. When false, the
/// key's current etag — or null when the key has no visible entry (a
/// compare-and-swap against a since-deleted key).
/// </param>
public readonly record struct KeyValuePutResult(bool Applied, long? ETag);
