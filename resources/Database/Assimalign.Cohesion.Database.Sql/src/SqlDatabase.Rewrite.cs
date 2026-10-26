using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql;

public sealed partial class SqlDatabase
{
    /// <summary>
    /// Rewrites a table's live rows with dense physical ordinals and rebuilds its indexes.
    /// </summary>
    /// <param name="schemaName">The decoded SQL schema name, for example <c>dbo</c>.</param>
    /// <param name="tableName">The decoded table name, without SQL delimiters.</param>
    /// <param name="cancellationToken">Cancels admission or preparation before publication.</param>
    /// <returns>Completion of the maintenance operation on the engine's provisioning worker.</returns>
    /// <exception cref="ArgumentException">A name is default or invalid.</exception>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    /// <exception cref="OperationCanceledException">Preparation was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline.</exception>
    /// <exception cref="DatabaseObjectLockedException">Session DDL, schema application or another maintenance operation owns admission.</exception>
    /// <exception cref="SqlCatalogException">The table is missing or its description changed while acquiring its exclusive lock.</exception>
    /// <remarks>
    /// This physical maintenance also accepts schema-owned tables. It preserves their logical
    /// definitions and identity allocation. Writers wait for the table's exclusive lock;
    /// older snapshots continue to read the retired layout until their pins end. Cancellation
    /// after durable publication cannot undo it. Interrupted preparation leaves the old table
    /// current and durable reclamation work for the abandoned owner; retry explicitly.
    /// </remarks>
    public ValueTask RewriteTableAsync(SqlObjectName schemaName, SqlObjectName tableName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!SqlObjectName.IsValid(schemaName.ToString(), out var schemaError)) { throw new ArgumentException(schemaError, nameof(schemaName)); }
        if (!SqlObjectName.IsValid(tableName.ToString(), out var tableError)) { throw new ArgumentException(tableError, nameof(tableName)); }
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOffline();
        if (!_catalog.TryGetTable(schemaName, tableName, out var table))
        {
            throw new SqlCatalogException("CS01051", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.TableMissing, schemaName, tableName));
        }
        return _schemaProvisioner.RewriteAsync(table, cancellationToken);
    }

    // Exact immutable descriptions let dependent engine operations revalidate a prior bind.
    internal ValueTask RewriteTableAsync(SqlCatalogTable table, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(table);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOffline();
        return _schemaProvisioner.RewriteAsync(table, cancellationToken);
    }
}
