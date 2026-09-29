using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Creates, reopens, discovers and drops storage for a graph engine.</summary>
/// <remarks>The engine owns returned storage. The supplied strategy remains caller-owned.</remarks>
public interface IGraphStorageStrategy
{
    /// <summary>Creates storage for a new logical database.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <param name="durability">The requested commit policy, or null for the storage default.</param>
    /// <returns>The storage whose ownership transfers to the engine.</returns>
    GraphStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability);

    /// <summary>Reopens storage with checkpointing deferred until engine recovery completes.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <param name="durability">The requested commit policy, or null for the storage default.</param>
    /// <returns>The storage whose ownership transfers to the engine.</returns>
    GraphStorage OpenStorage(DatabaseName databaseName, StorageCommitDurability? durability);

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
