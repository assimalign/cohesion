using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Creates, reopens, discovers and drops storage for a document engine.</summary>
/// <remarks>The engine owns returned storage. The supplied strategy remains caller-owned.</remarks>
public interface IDocumentStorageStrategy
{
    /// <summary>Creates storage for a new logical database.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <param name="durability">The requested commit policy, or null for the storage default.</param>
    /// <returns>The storage whose ownership transfers to the engine.</returns>
    DocumentStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability);

    /// <summary>Reopens storage with checkpointing deferred until engine recovery completes.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <param name="durability">The requested commit policy, or null for the storage default.</param>
    /// <returns>The storage whose ownership transfers to the engine.</returns>
    DocumentStorage OpenStorage(DatabaseName databaseName, StorageCommitDurability? durability);

    /// <summary>Drops all storage assets for a logical database.</summary>
    /// <param name="databaseName">The logical database name.</param>
    void DropStorage(DatabaseName databaseName);

    /// <summary>Determines whether a logical database has existing storage.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <returns>True when storage exists.</returns>
    bool StorageExists(DatabaseName databaseName);

    /// <summary>Enumerates the logical databases available to reopen.</summary>
    /// <returns>The stored database names.</returns>
    IEnumerable<DatabaseName> GetDatabaseNames();
}
