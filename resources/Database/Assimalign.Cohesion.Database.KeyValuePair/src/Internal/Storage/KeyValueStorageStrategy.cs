namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;

/// <summary>
/// Creates, opens and drops the storage file sets of a key-value engine's databases: each
/// database's data set and its catalog set are two storages created through it.
/// </summary>
/// <remarks>
/// <para>
/// The variants are the file-based strategy (under <see cref="KeyValueDatabaseEngineOptions.RootPath"/>)
/// and the in-memory one, both in this assembly, plus the durability and fault-injecting doubles
/// of this assembly's own tests, which derive through its test-only grant.
/// </para>
/// <para>
/// <b>A file set is a database and a set, never a derived name (#1392).</b> Each member takes the
/// database's name, in whatever spelling the caller used, and whether it means the catalog set.
/// Every variant maps the pair through <see cref="DatabaseFileNames"/>: one directory per database,
/// named for its lower-case storage name, holding <see cref="DataFileSet"/> and
/// <see cref="DatabaseFileNames.CatalogFileSet"/>. Until #1392 the catalog was the storage named
/// <c>{name}.catalog</c>, in the same namespace as database names, so a database named
/// <c>sales.catalog</c> was the catalog of <c>sales</c>.
/// </para>
/// <para>
/// <b>Internal (concrete-types plan, D9, phase 4, #1260).</b> Until phase 4 this was the public
/// <c>IKeyValueStorageStrategy</c> and an option property an application could set. No shipped
/// code outside this assembly implemented it, so it is an <c>internal abstract</c> base, and
/// <see cref="KeyValueDatabaseEngineOptions.StorageStrategy"/> and
/// <see cref="KeyValueDatabaseEngineBuilder.StorageStrategy"/> are internal with it.
/// </para>
/// </remarks>
internal abstract class KeyValueStorageStrategy
{
    /// <summary>
    /// The name of a key-value database's data file set in its directory: <c>keyvalue.dat</c>,
    /// <c>keyvalue.log</c> and <c>keyvalue.bak</c>.
    /// </summary>
    internal const string DataFileSet = "keyvalue";

    /// <summary>
    /// Gets the name of one of a database's file sets in its directory.
    /// </summary>
    /// <param name="catalog">True for the catalog set; false for the data set.</param>
    /// <returns><see cref="DatabaseFileNames.CatalogFileSet"/> or <see cref="DataFileSet"/>.</returns>
    internal static string GetFileSet(bool catalog) => catalog ? DatabaseFileNames.CatalogFileSet : DataFileSet;

    /// <summary>
    /// Gets how a file set is named in messages and in its storage header: the database's name for
    /// its data set, which the header keeps as the spelling the database was created with
    /// (<see cref="DatabaseFileNames.GetOpenedName"/>), and <c>{name}/catalog</c> for its catalog set.
    /// </summary>
    /// <param name="databaseName">The database name.</param>
    /// <param name="catalog">True for the catalog set; false for the data set.</param>
    /// <returns>The label.</returns>
    internal static string GetLabel(string databaseName, bool catalog)
        => catalog ? DatabaseFileNames.GetCatalogLabel(databaseName) : databaseName;

    /// <summary>
    /// Creates one new file set of a database. The data set is created first, and claims the
    /// database: a create of it fails when the database exists in any spelling.
    /// </summary>
    /// <param name="databaseName">The database name, in any spelling.</param>
    /// <param name="catalog">True for the catalog set; false for the data set.</param>
    /// <returns>A new <see cref="KeyValueStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when the file set, or for the data set the database, already exists.</exception>
    public abstract KeyValueStorage CreateStorage(string databaseName, bool catalog);

    /// <summary>
    /// Opens one existing file set of a database.
    /// </summary>
    /// <param name="databaseName">The database name, in any spelling.</param>
    /// <param name="catalog">True for the catalog set; false for the data set.</param>
    /// <returns>An existing <see cref="KeyValueStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when the file set does not exist.</exception>
    /// <remarks>
    /// Implementations that reopen persisted state must open with the open-time checkpoint
    /// deferred (<c>KeyValueStorage.Open(..., checkpointOnOpen: false)</c>): the engine runs
    /// transaction-recovery analysis over the recovered journal (classification reads lifecycle
    /// records an eager truncation would destroy) and checkpoints the storage itself once analysis
    /// completes.
    /// </remarks>
    public abstract KeyValueStorage OpenStorage(string databaseName, bool catalog);

    /// <summary>
    /// Drops every file set of a database: on disk, its directory.
    /// </summary>
    /// <param name="databaseName">The database name, in any spelling.</param>
    public abstract void DropStorage(string databaseName);

    /// <summary>
    /// Returns whether one file set of a database exists.
    /// </summary>
    /// <param name="databaseName">The database name, in any spelling.</param>
    /// <param name="catalog">True for the catalog set; false for the data set.</param>
    /// <returns><c>true</c> if the file set exists; otherwise <c>false</c>.</returns>
    public abstract bool StorageExists(string databaseName, bool catalog);
}
