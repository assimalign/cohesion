namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Raised when inserting a key into a unique index that already maps to an entry
/// visible to the writing transaction.
/// </summary>
public sealed class IndexUniqueViolationException : IndexException
{
    /// <summary>
    /// Initializes a new <see cref="IndexUniqueViolationException"/>.
    /// </summary>
    /// <param name="indexName">The unique index that rejected the key.</param>
    /// <param name="key">The duplicate key.</param>
    public IndexUniqueViolationException(string indexName, IndexKey key)
        : base($"Unique index '{indexName}' already contains an entry for key {key}.")
    {
        IndexName = indexName;
        Key = key;
    }

    /// <summary>
    /// Gets the name of the unique index that rejected the key.
    /// </summary>
    public string IndexName { get; }

    /// <summary>
    /// Gets the duplicate key.
    /// </summary>
    public IndexKey Key { get; }
}
