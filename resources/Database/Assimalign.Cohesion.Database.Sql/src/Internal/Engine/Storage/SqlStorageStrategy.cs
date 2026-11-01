using System.Threading;
using Assimalign.Cohesion.Database.Sql.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Creates, opens and drops the storage file sets of a SQL engine's databases: each database's
/// data set and its catalog set are two storages created through it.
/// </summary>
/// <remarks>
/// <para>
/// The variants are the file-based strategy (under <see cref="SqlDatabaseEngineOptions.RootPath"/>)
/// and the in-memory one, both in this assembly, plus the crash-capture and fault-injecting
/// doubles of this assembly's own tests, which derive through its test-only grant.
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
    /// The name of a SQL database's data file set in its directory: <c>sql.dat</c>, <c>sql.log</c>
    /// and <c>sql.bak</c>.
    /// </summary>
    internal const string DataFileSet = "sql";

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
    /// <returns>A new <see cref="SqlStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when the file set, or for the data set the database, already exists.</exception>
    public abstract SqlStorage CreateStorage(string databaseName, bool catalog);

    /// <summary>
    /// Opens one existing file set of a database.
    /// </summary>
    /// <param name="databaseName">The database name, in any spelling.</param>
    /// <param name="catalog">True for the catalog set; false for the data set.</param>
    /// <returns>An existing <see cref="SqlStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when the file set does not exist.</exception>
    /// <remarks>
    /// Implementations that reopen persisted state must open with the open-time checkpoint
    /// deferred (<c>SqlStorage.Open(..., checkpointOnOpen: false)</c>): the engine runs
    /// transaction-recovery analysis over the recovered journal (classification reads lifecycle
    /// records an eager truncation would destroy) and checkpoints the storage itself once analysis
    /// completes.
    /// </remarks>
    /// <param name="cancellationToken">Cancels physical recovery between records and pages.</param>
    public abstract SqlStorage OpenStorage(string databaseName, bool catalog, CancellationToken cancellationToken = default);

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
    // A strategy without a read-only probe cannot prove an incomplete create safe to adopt.
    internal virtual SqlStorageProbe? ProbeStorage(string databaseName, bool catalog) => null;

    // Memory strategies have no disk capacity limit. File strategies query the actual
    // database volume; tests supply capacity without filling a disk.
    internal virtual long GetAvailableMigrationBytes(string databaseName) => long.MaxValue;

    // File/volume allocation mode decides whether dirty pages need extra space.
    internal virtual bool SparseDataGrowth(string databaseName) => false;

    internal virtual SqlStorage RestartStorage(string databaseName, bool catalog)
        => throw new System.InvalidOperationException(ErrorMessages.CreationRestartUnavailable);
    internal const string CreationFileName = "sql.create";
    internal const string CreationSignature = "COHSQLCREATE1\n";

    internal virtual bool IsCreationPending(string databaseName) => false;

    internal virtual void CompleteCreation(string databaseName) { }
}
