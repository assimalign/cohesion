using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// Opens <see cref="ISqlCatalog"/> instances over a dedicated catalog storage
/// file set.
/// </summary>
/// <remarks>
/// The catalog owns its own storage instance — separate from the database's data
/// file set — so metadata records and user rows never share a scan space, while
/// still getting the same page/WAL durability. The engine composes one catalog
/// storage per database (by convention the database name suffixed with
/// <c>.catalog</c>).
/// </remarks>
public static class SqlCatalog
{
    /// <summary>
    /// Opens the catalog persisted in the given storage (an empty storage yields an
    /// empty catalog).
    /// </summary>
    /// <param name="storage">The dedicated catalog storage file set.</param>
    /// <returns>The catalog.</returns>
    public static ISqlCatalog Open(SqlStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return DefaultSqlCatalog.Open(storage);
    }

    /// <summary>
    /// Opens the catalog and establishes its database default string collation.
    /// </summary>
    /// <param name="storage">The dedicated catalog storage file set.</param>
    /// <param name="defaultCollation">
    /// The database default collation, adopted only by an empty catalog. Reopening a
    /// populated catalog keeps its persisted default, so the value is fixed for the
    /// lifetime of the database.
    /// </param>
    /// <returns>The catalog.</returns>
    /// <exception cref="SqlCatalogException">
    /// The catalog already contains tables under a different default. Their index keys
    /// are encoded through the collation they inherited, so it cannot be changed.
    /// </exception>
    public static ISqlCatalog Open(SqlStorage storage, Collation defaultCollation)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(defaultCollation);
        return DefaultSqlCatalog.Open(storage, defaultCollation);
    }

    /// <summary>
    /// Captures the table directory, index descriptions, and default collation atomically.
    /// </summary>
    /// <param name="catalog">The catalog to capture, as returned by <see cref="Open(SqlStorage)"/>.</param>
    /// <returns>A read-only capture unaffected by later catalog publications.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    /// <exception cref="InvalidCastException"><paramref name="catalog"/> was not produced by this class.</exception>
    /// <remarks>
    /// Declared here rather than on <see cref="ISqlCatalog"/> deliberately: the capture is an
    /// engine convenience over the catalog's own directory, so it stays off the contract every
    /// catalog implementation would otherwise have to honour.
    /// </remarks>
    public static ISqlCatalogSnapshot CaptureSnapshot(ISqlCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ((DefaultSqlCatalog)catalog).CaptureSnapshot();
    }

    /// <summary>
    /// Reserves a durable object identity for a table before its enforcing indexes are built.
    /// </summary>
    /// <param name="catalog">The catalog to reserve in, as returned by <see cref="Open(SqlStorage)"/>.</param>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key columns, or null for no primary key.</param>
    /// <param name="constraints">The foreign-key and check definitions.</param>
    /// <param name="owner">The table's ownership classification.</param>
    /// <param name="owningSchema">The compiled schema name for schema-owned tables; otherwise null.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The unpublished table definition with an assigned object identity.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A name is empty or whitespace, a constraint is null, or ownership metadata is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ownership classification is unsupported.</exception>
    /// <exception cref="InvalidCastException"><paramref name="catalog"/> was not produced by this class.</exception>
    /// <exception cref="SqlCatalogException">The table exists or its definition is invalid.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>
    /// Only the identity counter is persisted. The table remains invisible until
    /// <see cref="PublishTableAsync"/> succeeds; abandoning a reservation never reuses
    /// its identity. Callers serialize DDL, and a reservation does not lock the name.
    /// </remarks>
    public static ValueTask<SqlCatalogTable> ReserveTableAsync(
        ISqlCatalog catalog, string schema, string name,
        IReadOnlyList<SqlCatalogColumn> columns, IReadOnlyList<string>? primaryKeyColumns,
        IReadOnlyList<SqlCatalogConstraint> constraints, DatabaseObjectOwner owner,
        string? owningSchema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ((DefaultSqlCatalog)catalog).ReserveTableAsync(
            schema, name, columns, primaryKeyColumns, constraints, owner, owningSchema, cancellationToken);
    }

    /// <summary>
    /// Publishes a table definition and its new index descriptions with the complete physical
    /// registration set atomically.
    /// </summary>
    /// <param name="catalog">The catalog to publish into, as returned by <see cref="Open(SqlStorage)"/>.</param>
    /// <param name="table">The reserved table, or a replacement definition with the existing table's identity.</param>
    /// <param name="indexes">New index descriptions whose physical trees have already been committed.</param>
    /// <param name="registrations">The complete durable index registration set, replacing the stored set.</param>
    /// <param name="replaceExisting">Whether the definition replaces an existing table with the same identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing durable publication of all supplied metadata.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidCastException"><paramref name="catalog"/> was not produced by this class.</exception>
    /// <exception cref="SqlCatalogException">The identity was not reserved, or the identity, table definition, or index registrations are inconsistent.</exception>
    /// <exception cref="DatabaseTypeException">Metadata text contains invalid UTF-16.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>
    /// New definitions require a nonzero identity already allocated by this catalog.
    /// Callers serialize DDL and commit enforcing index trees before publication.
    /// Existing index descriptions are retained during replacement. The catalog
    /// owns metadata persistence; physical tree creation and row validation remain
    /// the caller's responsibility.
    /// </remarks>
    public static ValueTask PublishTableAsync(
        ISqlCatalog catalog, SqlCatalogTable table, IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations, bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ((DefaultSqlCatalog)catalog).PublishTableAsync(
            table, indexes, registrations, replaceExisting, cancellationToken);
    }

    /// <summary>
    /// Drops a persisted foreign-key or check constraint from a table.
    /// </summary>
    /// <param name="catalog">The catalog to update, as returned by <see cref="Open(SqlStorage)"/>.</param>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="constraintName">The constraint name, compared case-insensitively.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The durably updated table description.</returns>
    /// <exception cref="ArgumentNullException">A required name is null.</exception>
    /// <exception cref="ArgumentException">A required name is empty or whitespace.</exception>
    /// <exception cref="InvalidCastException"><paramref name="catalog"/> was not produced by this class.</exception>
    /// <exception cref="SqlCatalogException">The table or constraint does not exist.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static ValueTask<SqlCatalogTable> DropConstraintAsync(
        ISqlCatalog catalog, string schema, string name, string constraintName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ((DefaultSqlCatalog)catalog).DropConstraintAsync(schema, name, constraintName, cancellationToken);
    }

    /// <summary>
    /// Creates a schema-owned table directly for catalog persistence tests.
    /// Engine provisioning uses the public reserve/build/publish lifecycle.
    /// </summary>
    internal static ValueTask<SqlCatalogTable> CreateSchemaTableAsync(
        ISqlCatalog catalog,
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        string owningSchema,
        CancellationToken cancellationToken)
        => ((DefaultSqlCatalog)catalog).CreateTableAsync(
            schema, name, columns, primaryKeyColumns,
            DatabaseObjectOwner.Schema, owningSchema, cancellationToken);

    internal static ValueTask<SqlCatalogTable> CreateTableAsync(
        ISqlCatalog catalog, string schema, string name,
        IReadOnlyList<SqlCatalogColumn> columns, IReadOnlyList<string>? primaryKeyColumns,
        IReadOnlyList<SqlCatalogConstraint> constraints, DatabaseObjectOwner owner,
        string? owningSchema, CancellationToken cancellationToken)
        => ((DefaultSqlCatalog)catalog).CreateTableAsync(
            schema, name, columns, primaryKeyColumns, owner, owningSchema, cancellationToken, constraints);

    internal static ValueTask<SqlCatalogTable> AddConstraintAsync(
        ISqlCatalog catalog, string schema, string name, SqlCatalogConstraint constraint,
        CancellationToken cancellationToken)
        => ((DefaultSqlCatalog)catalog).AddConstraintAsync(schema, name, constraint, cancellationToken);
}
