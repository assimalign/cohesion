using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// Creates, reopens, discovers and drops the storage of a graph engine's databases, in place of
/// the engine's own file-set or in-memory storage.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns the storage a strategy returns, and does not dispose the strategy itself.
/// Without a strategy the engine creates each database's files under
/// <see cref="GraphDatabaseEngineOptions.RootPath"/>, or in memory; the strategy's variants are the
/// durability, fault-injecting and recording doubles of this assembly's own tests, which derive
/// through its test-only grant.
/// </para>
/// <para>
/// <b>Internal (concrete-types plan, D9, phase 4, #1260).</b> Until phase 4 this was the public
/// <c>IGraphStorageStrategy</c> and an option property an application could set. No shipped
/// code implemented it, so it is an <c>internal abstract</c> base, and
/// <see cref="GraphDatabaseEngineOptions.StorageStrategy"/> and
/// <see cref="GraphDatabaseEngineBuilder.StorageStrategy"/> are internal with it.
/// </para>
/// </remarks>
internal abstract class GraphStorageStrategy
{
    /// <summary>Creates storage for a new logical database.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <param name="durability">The requested commit policy, or null for the storage default.</param>
    /// <returns>The storage whose ownership transfers to the engine.</returns>
    public abstract GraphStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability);

    /// <summary>Reopens storage with checkpointing deferred until engine recovery completes.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <param name="durability">The requested commit policy, or null for the storage default.</param>
    /// <returns>The storage whose ownership transfers to the engine.</returns>
    public abstract GraphStorage OpenStorage(DatabaseName databaseName, StorageCommitDurability? durability);

    /// <summary>Drops all storage assets for a logical database.</summary>
    /// <param name="databaseName">The logical database name.</param>
    public abstract void DropStorage(DatabaseName databaseName);

    /// <summary>Determines whether a logical database has existing storage.</summary>
    /// <param name="databaseName">The logical database name.</param>
    /// <returns>True when storage exists.</returns>
    public abstract bool StorageExists(DatabaseName databaseName);

    /// <summary>Enumerates the logical databases available to reopen.</summary>
    /// <returns>The stored database names.</returns>
    public abstract IEnumerable<DatabaseName> GetDatabaseNames();
}
