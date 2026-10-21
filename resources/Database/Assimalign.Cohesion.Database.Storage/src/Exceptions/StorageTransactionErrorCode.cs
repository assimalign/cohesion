namespace Assimalign.Cohesion.Database.Storage;

/// <summary>Identifies storage transaction violations without inspecting message text.</summary>
public enum StorageTransactionErrorCode
{
    /// <summary>A general transaction violation with no more specific reason.</summary>
    Unspecified = 0,

    /// <summary>The transaction belongs to another storage instance.</summary>
    ForeignStorage = 1,
}
