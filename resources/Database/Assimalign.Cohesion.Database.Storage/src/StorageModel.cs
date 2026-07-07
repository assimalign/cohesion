namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Identifies which database model a storage resource serves. Persisted in the
/// storage file header, so values are append-only and never renumbered.
/// </summary>
public enum StorageModel
{
    /// <summary>A custom or engine-specific storage layout.</summary>
    Custom = 0,

    /// <summary>Row-oriented relational storage (SQL model).</summary>
    Sql,

    /// <summary>Document storage (Documents model).</summary>
    Document,

    /// <summary>Ordered key-value storage (KeyValuePair model).</summary>
    KeyValue,

    /// <summary>Large-object storage (Blob model).</summary>
    Blob,

    /// <summary>Property-graph storage (Graph model).</summary>
    Graph,
}
