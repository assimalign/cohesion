using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    /// <summary>Renames a table within its schema and its dependent foreign-key metadata in one catalog transaction.</summary>
    /// <param name="expected">The current immutable table definition; a stale definition is refused.</param>
    /// <param name="newName">The unqualified new name.</param>
    /// <param name="checkExpressions">Canonical CHECK text rendered by the engine for every CHECK on the renamed table.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The durably published table definition.</returns>
    /// <exception cref="ArgumentException">A required name is blank.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">The definition is stale, a name collides, or CHECK metadata is incomplete or too large.</exception>
    /// <exception cref="DatabaseTypeException">Metadata text contains invalid UTF-16.</exception>
    /// <exception cref="StorageException">The catalog cannot persist the change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before publication.</exception>
    public ValueTask<SqlCatalogTable> RenameTableAsync(
        SqlCatalogTable expected,
        string newName,
        IReadOnlyDictionary<string, string> checkExpressions,
        CancellationToken cancellationToken = default)
    {
        return RenameCoreAsync(expected, null, newName, checkExpressions, null, cancellationToken);
    }

    /// <summary>Renames a table within its schema and its dependent foreign-key metadata in one catalog transaction.</summary>
    /// <param name="expected">The current immutable table definition; a stale definition is refused.</param>
    /// <param name="newName">The unqualified new name.</param>
    /// <param name="checkExpressions">Canonical CHECK text rendered by the engine for every CHECK on the renamed table.</param>
    /// <param name="publication">The expected script target and exact preceding receipt and generation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The durably published table definition.</returns>
    /// <exception cref="ArgumentException">A required name is blank.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">The definition is stale, a name collides, or CHECK metadata is incomplete or too large.</exception>
    /// <exception cref="DatabaseTypeException">Metadata text contains invalid UTF-16.</exception>
    /// <exception cref="StorageException">The catalog cannot persist the change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before publication.</exception>
    public ValueTask<SqlCatalogTable> RenameTableAsync(
        SqlCatalogTable expected,
        string newName,
        IReadOnlyDictionary<string, string> checkExpressions,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return RenameCoreAsync(expected, null, newName, checkExpressions, publication, cancellationToken);
    }

    /// <summary>Renames a column and its dependent foreign-key and index metadata in one catalog transaction.</summary>
    /// <param name="expected">The current immutable table definition; a stale definition is refused.</param>
    /// <param name="columnName">The existing column name.</param>
    /// <param name="newName">The unqualified new name.</param>
    /// <param name="checkExpressions">Canonical CHECK text rendered by the engine for every CHECK on the renamed table.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The durably published table definition.</returns>
    /// <exception cref="ArgumentException">A required name is blank.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">The definition is stale, a name collides, or CHECK metadata is incomplete or too large.</exception>
    /// <exception cref="DatabaseTypeException">Metadata text contains invalid UTF-16.</exception>
    /// <exception cref="StorageException">The catalog cannot persist the change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before publication.</exception>
    public ValueTask<SqlCatalogTable> RenameColumnAsync(
        SqlCatalogTable expected,
        string columnName,
        string newName,
        IReadOnlyDictionary<string, string> checkExpressions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        return RenameCoreAsync(expected, columnName, newName, checkExpressions, null, cancellationToken);
    }

    /// <summary>Renames a column and its dependent foreign-key and index metadata in one catalog transaction.</summary>
    /// <param name="expected">The current immutable table definition; a stale definition is refused.</param>
    /// <param name="columnName">The existing column name.</param>
    /// <param name="newName">The unqualified new name.</param>
    /// <param name="checkExpressions">Canonical CHECK text rendered by the engine for every CHECK on the renamed table.</param>
    /// <param name="publication">The expected script target and exact preceding receipt and generation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The durably published table definition.</returns>
    /// <exception cref="ArgumentException">A required name is blank.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">The definition is stale, a name collides, or CHECK metadata is incomplete or too large.</exception>
    /// <exception cref="DatabaseTypeException">Metadata text contains invalid UTF-16.</exception>
    /// <exception cref="StorageException">The catalog cannot persist the change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before publication.</exception>
    public ValueTask<SqlCatalogTable> RenameColumnAsync(
        SqlCatalogTable expected,
        string columnName,
        string newName,
        IReadOnlyDictionary<string, string> checkExpressions,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        return RenameCoreAsync(expected, columnName, newName, checkExpressions, publication, cancellationToken);
    }

    private ValueTask<SqlCatalogTable> RenameCoreAsync(SqlCatalogTable expected, string? columnName, string newName,
        IReadOnlyDictionary<string, string> checkExpressions, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(checkExpressions);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            if (!_tables.TryGetValue((expected.Schema, expected.Name), out var original) || !ReferenceEquals(original.Table, expected))
            {
                throw new SqlCatalogException("CS04064", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogRenameCoreAsyncSqlCatalog, expected.Schema, expected.Name));
            }
            EnsureNoViewReads(expected, columnName, rename: true);
            if (columnName is null)
            {
                if (_tables.ContainsKey((expected.Schema, newName)) || _views.ContainsKey(SqlObjectIdentity.View(expected.Schema, newName)))
                {
                    throw new SqlCatalogException("CS03049", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RenameTableCollision, expected.Schema, newName));
                }
            }
            else
            {
                if (expected.FindColumn(columnName) is null)
                {
                    throw new SqlCatalogException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnNameMissing, expected.Schema, expected.Name, columnName));
                }
                if (expected.FindColumn(newName) is not null)
                {
                    throw new SqlCatalogException("CS03061", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RenameColumnCollision, newName, expected.Name));
                }
            }
            var checks = expected.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Check).ToArray();
            if (checks.Length != checkExpressions.Count || checks.Any(check => !checkExpressions.TryGetValue(check.Name, out var text) || string.IsNullOrWhiteSpace(text)))
            {
                throw new SqlCatalogException("CS05060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogRenameCoreAsyncSqlCatalog2, expected.Name));
            }

            string RenameColumn(string name) => columnName is not null && string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase) ? newName : name;
            bool References(SqlCatalogConstraint constraint) => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                string.Equals(constraint.ReferencedSchema, expected.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(constraint.ReferencedTable, expected.Name, StringComparison.OrdinalIgnoreCase);
            var tables = new List<(TableSlot Old, SqlCatalogTable Table, byte[] Record)>();
            foreach (var slot in _tables.Values)
            {
                bool own = ReferenceEquals(slot, original);
                if (!own && !slot.Table.Constraints.Any(References)) { continue; }
                var table = slot.Table;
                var constraints = table.Constraints.Select(constraint => new SqlCatalogConstraint(constraint.Name, constraint.Kind,
                    own ? constraint.Columns.Select(RenameColumn).ToArray() : constraint.Columns,
                    constraint.ReferencedSchema,
                    References(constraint) && columnName is null ? newName : constraint.ReferencedTable,
                    References(constraint) ? constraint.ReferencedColumns.Select(RenameColumn).ToArray() : constraint.ReferencedColumns,
                    own && constraint.Kind == SqlCatalogConstraintKind.Check ? checkExpressions[constraint.Name] : constraint.CheckExpression,
                    constraint.OnDelete, constraint.CheckFunctionCalls)).ToArray();
                var columns = own && columnName is not null ? table.Columns.Select(column => new SqlCatalogColumn(
                    RenameColumn(column.Name), column.Type, column.IsNullable, column.DefaultLiteral, column.Collation,
                    column.DomainName, column.DefaultFunctionCalls, column.Identity, column.MissingValueLiteral) { RecastDefault = column.RecastDefault }).ToArray() : table.Columns;
                var replacement = new SqlCatalogTable(table.ObjectId, table.Schema, own && columnName is null ? newName : table.Name,
                    columns, own ? table.PrimaryKeyColumns.Select(RenameColumn).ToArray() : table.PrimaryKeyColumns,
                    table.Owner, table.OwningSchema, constraints, table.DroppedColumnOrdinals);
                ValidateColumns(replacement.Schema, replacement.Name, replacement.Columns, replacement.PrimaryKeyColumns);
                ValidateConstraints(replacement);
                tables.Add((slot, replacement, EncodeTable(replacement)));
            }
            var indexes = new List<(IndexSlot Old, SqlCatalogIndex Index, byte[] Record)>();
            if (columnName is not null)
            {
                foreach (var slot in _indexes.Values.Where(slot => slot.Index.TableObjectId == expected.ObjectId))
                {
                    var index = slot.Index;
                    var replacement = new SqlCatalogIndex(index.TableObjectId, index.Name, index.ColumnNames.Select(RenameColumn).ToArray(),
                        index.IsUnique, index.Owner, index.OwningSchema, index.IsPrimaryKey);
                    indexes.Add((slot, replacement, EncodeIndex(replacement)));
                }
            }
            // Encode every record before the first write. Cache publication follows the one durable commit.
            using var transaction = _storage.BeginTransaction();
            var tableSlots = tables.Select(item => new TableSlot(item.Table, UpsertRecord(transaction, item.Old.Location, item.Record))).ToArray();
            var indexSlots = indexes.Select(item => new IndexSlot(item.Index, UpsertRecord(transaction, item.Old.Location, item.Record))).ToArray();
            CommitCatalogChange(transaction, publication: publication, actualObject: SqlObjectIdentity.Table(expected.Schema, expected.Name));
            _tables.Remove((expected.Schema, expected.Name));
            foreach (var slot in tableSlots) { _tables[(slot.Table.Schema, slot.Table.Name)] = slot; }
            foreach (var slot in indexSlots) { _indexes[(slot.Index.TableObjectId, slot.Index.Name)] = slot; }
            return new ValueTask<SqlCatalogTable>(tableSlots.Single(slot => slot.Table.ObjectId == expected.ObjectId).Table);
        }
    }
}
