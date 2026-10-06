namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;

/// <summary>
/// Creates, opens and drops the storage file sets of a SQL engine's databases: each database's
/// data set and its <c>.catalog</c> set are two storages created through it.
/// </summary>
/// <remarks>
/// <para>
/// The variants are the file-based strategy (under <see cref="SqlDatabaseEngineOptions.RootPath"/>)
/// and the in-memory one, both in this assembly, plus the crash-capture and fault-injecting
/// doubles of this assembly's own tests, which derive through its test-only grant.
/// </para>
/// <para>
/// <b>Internal (concrete-types plan, D9, phase 4, row 84).</b> Until phase 4 this was the public
/// <c>ISqlStorageStrategy</c> and an option property an application could set. No shipped code
/// outside this assembly implemented it, so it is an <c>internal abstract</c> base, and
/// <see cref="SqlDatabaseEngineOptions.StorageStrategy"/> and
/// <see cref="SqlDatabaseEngineBuilder.StorageStrategy"/> are internal with it.
/// </para>
/// </remarks>
internal abstract class SqlStorageStrategy
{
    /// <summary>
    /// Creates new storage streams for a database.
    /// </summary>
    /// <param name="databaseName">The name of the storage to create (a database's name, or its catalog's).</param>
    /// <returns>A new <see cref="SqlStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when storage already exists for the specified name.</exception>
    public abstract SqlStorage CreateStorage(string databaseName);

    /// <summary>
    /// Opens existing storage streams for a database.
    /// </summary>
    /// <param name="databaseName">The name of the storage to open.</param>
    /// <returns>An existing <see cref="SqlStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when storage does not exist for the specified name.</exception>
    /// <remarks>
    /// Implementations that reopen persisted state must open with the open-time checkpoint
    /// deferred (<c>SqlStorage.Open(..., checkpointOnOpen: false)</c>): the engine runs
    /// transaction-recovery analysis over the recovered journal (classification reads lifecycle
    /// records an eager truncation would destroy) and checkpoints the storage itself once analysis
    /// completes.
    /// </remarks>
    public abstract SqlStorage OpenStorage(string databaseName);

    /// <summary>
    /// Drops all storage assets for a database.
    /// </summary>
    /// <param name="databaseName">The name of the storage to drop.</param>
    public abstract void DropStorage(string databaseName);

    /// <summary>
    /// Returns whether storage exists for the specified name.
    /// </summary>
    /// <param name="databaseName">The name of the storage to check.</param>
    /// <returns><c>true</c> if storage exists; otherwise <c>false</c>.</returns>
    public abstract bool StorageExists(string databaseName);
}
