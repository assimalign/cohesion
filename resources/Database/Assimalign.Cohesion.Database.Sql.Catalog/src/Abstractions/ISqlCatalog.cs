using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The relational catalog of one SQL database: schemas, tables, columns,
/// constraints, and the physical registrations of the database's indexes —
/// persisted through the storage kernel so metadata gets the same durability as
/// data.
/// </summary>
/// <remarks>
/// DDL operations are self-committing: each runs in its own storage transaction and
/// is durable when the call returns. Interleaving DDL with an open DML transaction
/// is deliberately not supported in the MVP (the catalog cache updates on commit,
/// and half-visible schema changes are a correctness trap) — the engine serializes
/// DDL per database.
/// </remarks>
public interface ISqlCatalog
{
    /// <summary>
    /// Gets every table in the catalog.
    /// </summary>
    IReadOnlyList<SqlCatalogTable> Tables { get; }

    /// <summary>
    /// Finds a table by schema and name (case-insensitive).
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="table">When this method returns true, the table.</param>
    /// <returns>True when the table exists; otherwise false.</returns>
    bool TryGetTable(string schema, string name, out SqlCatalogTable table);

    /// <summary>
    /// Creates a table. The definition's object identity is assigned by the catalog.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name, unique within the schema.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key column names, when declared.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created table with its assigned object identity.</returns>
    /// <exception cref="SqlCatalogException">A table with the name already exists, or the definition is invalid.</exception>
    ValueTask<SqlCatalogTable> CreateTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a table.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="SqlCatalogException">The table does not exist.</exception>
    ValueTask DropTableAsync(string schema, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a column to a table.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="column">The column to add.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The updated table description.</returns>
    /// <exception cref="SqlCatalogException">The table does not exist or already has the column.</exception>
    ValueTask<SqlCatalogTable> AddColumnAsync(string schema, string name, SqlCatalogColumn column, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a column from a table.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="columnName">The column to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The updated table description.</returns>
    /// <exception cref="SqlCatalogException">The table or column does not exist, or the column is part of the primary key.</exception>
    ValueTask<SqlCatalogTable> DropColumnAsync(string schema, string name, string columnName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the secondary indexes declared on a table.
    /// </summary>
    /// <param name="tableObjectId">The table's object identity.</param>
    /// <returns>The table's index descriptions; empty when it has none.</returns>
    IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong tableObjectId);

    /// <summary>
    /// Finds an index by owning table and name (case-insensitive).
    /// </summary>
    /// <param name="tableObjectId">The table's object identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="index">When this method returns true, the index description.</param>
    /// <returns>True when the index exists; otherwise false.</returns>
    bool TryGetIndex(ulong tableObjectId, string name, out SqlCatalogIndex index);

    /// <summary>
    /// Creates an index description and persists the given physical registrations
    /// in the same self-committing catalog transaction — the schema description and
    /// the tree registration must never tear apart across a crash (a registration
    /// without a description is an unused tree; a description without a
    /// registration would promise uniqueness no tree enforces).
    /// </summary>
    /// <param name="index">The index description to persist.</param>
    /// <param name="registrations">The full registration set to persist alongside (replaces the stored set).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created index description.</returns>
    /// <exception cref="SqlCatalogException">The table does not exist, a key column does not exist, or an index with the name already exists on the table.</exception>
    ValueTask<SqlCatalogIndex> CreateIndexAsync(SqlCatalogIndex index, IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops an index description and persists the given physical registrations in
    /// the same self-committing catalog transaction (see
    /// <see cref="CreateIndexAsync"/> for why the two writes are atomic).
    /// </summary>
    /// <param name="tableObjectId">The table's object identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="registrations">The full registration set to persist alongside (replaces the stored set).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="SqlCatalogException">The index does not exist.</exception>
    ValueTask DropIndexAsync(ulong tableObjectId, string name, IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the physical index registrations exported by the index manager, so
    /// indexes re-attach when the database reopens.
    /// </summary>
    /// <param name="registrations">The registrations to persist (replaces the stored set).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask SaveIndexRegistrationsAsync(IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the persisted index registrations.
    /// </summary>
    /// <returns>The stored registrations, empty when none were saved.</returns>
    IReadOnlyList<BTreeIndexRegistration> GetIndexRegistrations();

    /// <summary>
    /// Gets the record-space format version of the database's data storage: 1 =
    /// the pre-MVCC unstamped row layout (the value reported when no marker is
    /// persisted), 2 = MVCC-stamped records. The catalog is the marker's home
    /// because rows are not self-describing across format changes — the engine
    /// reads this at open and upgrades a version-1 record space in place.
    /// </summary>
    int RecordSpaceFormatVersion { get; }

    /// <summary>
    /// Persists the record-space format version. Self-committing, like every
    /// catalog write; called by the engine after a record-space upgrade (or at
    /// database creation, when the space is born on the current format).
    /// </summary>
    /// <param name="version">The format version to persist.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask SetRecordSpaceFormatVersionAsync(int version, CancellationToken cancellationToken = default);
}
