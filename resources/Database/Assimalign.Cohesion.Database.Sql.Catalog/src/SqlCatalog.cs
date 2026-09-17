using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Indexing;

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
    /// Creates a schema-owned table through the engine's internal provisioning path,
    /// without adding a capability to the public catalog contract.
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

    internal static ValueTask<SqlCatalogTable> ReserveTableAsync(
        ISqlCatalog catalog, string schema, string name,
        IReadOnlyList<SqlCatalogColumn> columns, IReadOnlyList<string>? primaryKeyColumns,
        IReadOnlyList<SqlCatalogConstraint> constraints, DatabaseObjectOwner owner,
        string? owningSchema, CancellationToken cancellationToken)
        => ((DefaultSqlCatalog)catalog).ReserveTableAsync(
            schema, name, columns, primaryKeyColumns, constraints, owner, owningSchema, cancellationToken);

    internal static ValueTask PublishTableAsync(
        ISqlCatalog catalog, SqlCatalogTable table, IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken,
        bool replaceExisting = false)
        => ((DefaultSqlCatalog)catalog).PublishTableAsync(table, indexes, registrations, cancellationToken, replaceExisting);

    internal static ValueTask<SqlCatalogTable> AddConstraintAsync(
        ISqlCatalog catalog, string schema, string name, SqlCatalogConstraint constraint,
        CancellationToken cancellationToken)
        => ((DefaultSqlCatalog)catalog).AddConstraintAsync(schema, name, constraint, cancellationToken);

    internal static ValueTask<SqlCatalogTable> DropConstraintAsync(
        ISqlCatalog catalog, string schema, string name, string constraintName,
        CancellationToken cancellationToken)
        => ((DefaultSqlCatalog)catalog).DropConstraintAsync(schema, name, constraintName, cancellationToken);
}
