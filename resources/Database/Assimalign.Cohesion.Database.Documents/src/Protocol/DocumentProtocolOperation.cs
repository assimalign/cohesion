namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Operations carried by a Documents Command frame.</summary>
public enum DocumentProtocolOperation : byte
{
    /// <summary>Reads a document by identity.</summary>
    Get = 1,
    /// <summary>Inserts or replaces a document.</summary>
    Put = 2,
    /// <summary>Deletes a document by identity.</summary>
    Delete = 3,
    /// <summary>Creates a collection.</summary>
    CreateCollection = 4,
    /// <summary>Opens a collection.</summary>
    GetCollection = 5,
    /// <summary>Drops a collection and its contents.</summary>
    DropCollection = 6,
    /// <summary>Lists collections in ordinal name order.</summary>
    ListCollections = 7,
}
