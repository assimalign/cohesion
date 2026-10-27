using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action? ReferencedTablesLocked { get; set; }
    private async Task CreateConstrainedTableAsync(SqlCreateTablePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // Every DEFAULT must convert to its column before anything is reserved, as ADD COLUMN
        // requires: a default the column cannot store would otherwise be published and fail
        // every later INSERT that omits the column.
        var defaults = new SqlBoundExpression?[plan.Columns.Count];
        for (int ordinal = 0; ordinal < plan.Columns.Count; ordinal++)
        {
            var column = plan.Columns[ordinal];
            if (column.DefaultLiteral is not null)
            {
                defaults[ordinal] = BindDefaultExpression(column, $"DEFAULT of column '{column.Name}' on table '{plan.Schema}.{plan.Name}'", _definitions.Functions, _catalog.DefaultCollation, cancellationToken, declaring: true);
            }
        }

        var provisional = new SqlCatalogTable(0, plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey);
        var parents = new SortedDictionary<ulong, SqlCatalogTable>();
        var constraints = BindConstraints(provisional, plan.Constraints, parents);
        await LockReferencedTablesAsync(parents, statement, cancellationToken).ConfigureAwait(false);
        // Rebind after waiting: a parent definition might have changed while acquiring its lock.
        constraints = BindConstraints(provisional, plan.Constraints);
        // Durable, but not a self-commit the session counts (#1272): the reservation persists only
        // the object-id counter, and the table stays invisible until the publish.
        var table = await (statement.ScriptReceipt is null ? _catalog.ReserveTableAsync(plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey, constraints,
            statement.ProvisioningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema,
            statement.ProvisioningSchema, cancellationToken) : _catalog.ReserveTableAsync(plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey, constraints,
            statement.ProvisioningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema,
            statement.ProvisioningSchema, statement.ScriptReceipt, cancellationToken)).ConfigureAwait(false);
        SqlCatalogScriptPublication? publication = null;
        if (statement.ScriptReceipt is { } expected)
        {
            // Reservation advances exactly one generation and preserves the completed prefix.
            // Derive that expected state instead of accepting a fresh snapshot that could include
            // an intervening publication through the public catalog API.
            var previous = expected.Previous!;
            ulong reservedGeneration = checked(expected.ExpectedGeneration + 1);
            var reserved = new SqlCatalogScript(previous.Name, previous.ContentHash, previous.StatementCount,
                previous.CompletedStatements, previous.BaseGeneration, reservedGeneration, previous.SchemaHash,
                previous.Objects, previous.StatementObjects, previous.StatementParameterTypes);
            publication = new SqlCatalogScriptPublication(expected.Name, expected.ContentHash, expected.StatementCount,
                reserved, reservedGeneration, expected.SchemaHash, expected.ExpectedObject);
        }

        // Lock before publication, and retain the apply's owner through its final marker
        // or compensation. PostgreSQL tablecmds.c:1112-1118,1390-1394 likewise retains
        // AccessExclusiveLock on the newly created relation through transaction end.
        await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
            LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        // Bind the persisted definitions of the version about to be published, from their
        // stored text, so no write to the new table ever parses them.
        _definitions.GetDeclared(table, defaults, cancellationToken);
        await PublishConstrainedTableAsync(table, plan.Constraints, statement, cancellationToken, replaceExisting: false, publication).ConfigureAwait(false);
    }

    private async Task LockReferencedTablesAsync(SortedDictionary<ulong, SqlCatalogTable> parents, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        foreach (ulong parent in parents.Keys)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner, LockResource.Object(parent),
                LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        }
        ReferencedTablesLocked?.Invoke();
        // Object locks protect identities, not names. Rebinding a replacement after
        // waiting on its retired owner's lock would read uncommitted parent rows.
        foreach (var parent in parents.Values)
        {
            if (!_catalog.TryGetTable(parent.Schema, parent.Name, out var current))
            {
                throw new SqlExecutionException(SqlExecutionException.SerializationFailureCode,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TableIdentityReplaced, parent.Schema, parent.Name));
            }
            EnsureSameIdentity(parent, current);
        }
    }

    // Empty CREATE trees and ALTER backfills are durable before the single catalog
    // publish. A crash can leak unpublished tree pages, but cannot expose a table
    // whose declared uniqueness has no enforcing index.
    private async Task PublishConstrainedTableAsync(SqlCatalogTable table, IReadOnlyList<SqlConstraintDefinition> definitions,
        SqlStatementContext statement, CancellationToken cancellationToken, bool replaceExisting, SqlCatalogScriptPublication? publication = null)
    {
        publication ??= statement.ScriptReceipt;
        var indexes = new List<SqlCatalogIndex>();
        bool primaryCreated = false;
        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (definition.Kind is not (SqlConstraintKind.Unique or SqlConstraintKind.PrimaryKey))
            {
                continue;
            }

            if (definition.Kind == SqlConstraintKind.PrimaryKey && primaryCreated)
            {
                continue;
            }

            if (definition.Kind == SqlConstraintKind.PrimaryKey)
            {
                primaryCreated = true;
            }

            var columns = definition.Kind == SqlConstraintKind.PrimaryKey ? table.PrimaryKeyColumns : definition.Columns;
            indexes.Add(new SqlCatalogIndex(table.ObjectId, ConstraintName(table, definition, i), columns, true, table.Owner, table.OwningSchema, isPrimaryKey: definition.Kind == SqlConstraintKind.PrimaryKey));
        }
        if (!replaceExisting && !primaryCreated && table.PrimaryKeyColumns.Count > 0)
        {
            indexes.Add(new SqlCatalogIndex(table.ObjectId, $"PrimaryKey_{table.Name}", table.PrimaryKeyColumns, true, table.Owner, table.OwningSchema, isPrimaryKey: true));
        }

        foreach (var metadata in indexes)
        {
            foreach (string name in metadata.ColumnNames)
            {
                var column = table.Columns[FindColumnOrdinal(table, name)];
                if (column.Type.Type is Types.DatabaseType.String or Types.DatabaseType.Character or Types.DatabaseType.Json
                    && !(column.Collation ?? _catalog.DefaultCollation).IsIndexBacked)
                {
                    throw new SqlExecutionException(SqlExecutionException.UnsupportedCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ConstraintCollationUnsupported, name));
                }
            }
        }

        var created = new List<string>();
        int commitsBeforePublication = statement.Metrics.SelfCommits;
        try
        {
            if (indexes.Count > 0)
            {
                if (statement.ProvisioningProgress is not null)
                {
                    foreach (var metadata in indexes)
                    {
                        await BuildDeferredIndexAsync(table, new IndexDefinition(metadata.Name, IndexKind.BTree, true),
                            metadata.ColumnNames.Select(column => FindColumnOrdinal(table, column)).ToArray(), statement,
                            () => created.Add(metadata.Name), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Durable, but not a self-commit the session counts (#1272): the trees are orphaned
                    // pages until the publish below describes them.
                    await statement.Coordinator.ApplyStatementAsync<bool>(statement.Transaction, async bracket =>
                    {
                        foreach (var metadata in indexes)
                        {
                            var index = await _indexManager.CreateIndexAsync(statement.Transaction, table.ObjectId,
                                new IndexDefinition(metadata.Name, IndexKind.BTree, true), cancellationToken).ConfigureAwait(false);
                            created.Add(metadata.Name);
                            var ordinals = metadata.ColumnNames.Select(column => FindColumnOrdinal(table, column)).ToArray();
                            foreach (var version in ScanVersions(table, cancellationToken))
                            {
                                var key = BuildIndexKey(table, ordinals, version.Values);
                                await index.InsertVersionAsync(bracket, key, SqlRecordLocation.Pack(version.Location.PageId, version.Location.SlotIndex),
                                    version.Writer, version.Deleter, cancellationToken).ConfigureAwait(false);
                            }
                            await ValidateBuiltUniqueIndexAsync(table, index, statement, cancellationToken).ConfigureAwait(false);
                        }
                        return true;
                    }, durable: true, cancellationToken).ConfigureAwait(false);
                }
            }
            await SelfCommitAsync(statement, (publication is null ? _catalog.PublishTableAsync(table, indexes, PublishedRegistrations(indexes),
                replaceExisting, cancellationToken) : _catalog.PublishTableAsync(table, indexes, PublishedRegistrations(indexes),
                replaceExisting, publication, cancellationToken))).ConfigureAwait(false);
        }
        catch when (statement.Metrics.SelfCommits == commitsBeforePublication)
        {
            foreach (string index in created)
            {
                await _indexManager.DropIndexAsync(statement.Transaction, table.ObjectId, index, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<DatabaseResult> ExecuteAddConstraintAsync(SqlAddConstraintPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // The backfill below validates every existing row against latest state, so
        // it needs table-grain exclusivity, not the row-grain referential locks
        // DML uses: the Exclusive lock on this table taken here, plus the
        // Exclusive locks on every referenced parent taken by
        // LockReferencedTablesAsync once the constraint has been bound.
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE ADD CONSTRAINT", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        if (plan.Constraint.Kind == SqlConstraintKind.PrimaryKey && plan.Table.PrimaryKeyColumns.Count > 0)
        {
            throw SqlConstraintDeclaration.MultiplePrimaryKeys(plan.Table.Name, ConstraintName(plan.Table, plan.Constraint, 0));
        }

        var parents = new SortedDictionary<ulong, SqlCatalogTable>();
        var constraints = BindConstraints(plan.Table, [plan.Constraint], parents);
        await LockReferencedTablesAsync(parents, statement, cancellationToken).ConfigureAwait(false);
        constraints = BindConstraints(plan.Table, [plan.Constraint]);
        var primary = plan.Constraint.Kind == SqlConstraintKind.PrimaryKey ? plan.Constraint.Columns : plan.Table.PrimaryKeyColumns;
        var columns = plan.Table.Columns.Select(column => primary.Contains(column.Name, StringComparer.OrdinalIgnoreCase)
            ? new SqlCatalogColumn(column.Name, column.Type, false, column.DefaultLiteral, column.Collation, column.DomainName, column.DefaultFunctionCalls, column.Identity, column.MissingValueLiteral) { RecastDefault = column.RecastDefault } : column).ToArray();
        var replacement = new SqlCatalogTable(plan.Table.ObjectId, plan.Table.Schema, plan.Table.Name, columns,
            primary, plan.Table.Owner, plan.Table.OwningSchema, plan.Table.Constraints.Concat(constraints).ToArray(),
            plan.Table.DroppedColumnOrdinals);
        var snapshot = ConstraintCurrentSnapshot(statement);
        long total = statement.ProvisioningProgress is null ? 0 : Scan(plan.Table, statement, cancellationToken, snapshot).LongCount();
        _definitions.Get(replacement, cancellationToken);
        long processed = 0;
        statement.ProvisioningProgress?.Invoke(0, total);
        foreach (var (_, row) in Scan(plan.Table, statement, cancellationToken, snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < columns.Length; i++)
            {
                CoerceForColumn(row[i], columns[i]);
            }
            ValidateRows(replacement, [row], statement, cancellationToken, current: true);
            statement.ProvisioningProgress?.Invoke(++processed, total);
        }

        // The backfill binds the replacement version, which is the instance published below.
        await PublishConstrainedTableAsync(replacement, [plan.Constraint], statement, cancellationToken, replaceExisting: true).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private async Task<DatabaseResult> ExecuteAddColumnAsync(SqlAddColumnPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Schema, plan.Name, "ALTER TABLE ADD COLUMN", cancellationToken).ConfigureAwait(false);
        _catalog.TryGetTable(plan.Schema, plan.Name, out var table);
        if (table.FindColumn(plan.Column.Name) is not null)
        {
            throw new SqlExecutionException(SqlExecutionException.DuplicateColumnCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutionColumnExists, plan.Column.Name));
        }

        if (plan.Column.Identity is not null && Scan(table, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Any())
        {
            throw SqlPlanningException.IdentityViolation(plan.Column.Name, ErrorMessages.IdentityBackfillForbidden);
        }

        // Validate even on an empty table: publishing an unusable default would
        // defer the failure until a later INSERT. No catalog or row writes occur
        // until every default and existing-row constraint has been checked.
        var column = plan.Column;
        SqlBoundExpression? boundDefault = null;
        if (column.DefaultLiteral is not null)
        {
            var expression = SqlPersistedExpression.Load(column.DefaultLiteral, "ADD COLUMN DEFAULT");
            var scope = new SqlExpressionEvaluator([], null, defaultCollation: _catalog.DefaultCollation,
                functions: _definitions.Functions, cancellationToken: cancellationToken);
            SqlDefinitionBinder.PinCalls(expression, scope, column.DefaultFunctionCalls);
            SqlPersistedExpression.Bind(expression, scope, declaring: true);
            var volatility = DefaultVolatility(expression, scope);
            if (volatility == SqlFunctionVolatility.Volatile &&
                Scan(table, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Any())
            {
                throw new SqlPlanningException("CS03027", string.Format(CultureInfo.CurrentCulture, ErrorMessages.VolatileDefaultBackfill, column.Name));
            }
            boundDefault = BindDefaultExpression(column, $"DEFAULT of column '{column.Name}' on table '{plan.Schema}.{plan.Name}'",
                _definitions.Functions, _catalog.DefaultCollation, cancellationToken, declaring: true);
            string? missing = volatility == SqlFunctionVolatility.Volatile ? null
                : SqlPersistedExpression.IsDefaultLiteral(expression) ? column.DefaultLiteral
                : FrozenDefaultLiteral(DeclaredDefaultValue(boundDefault, column, cancellationToken, statement.TransactionStart), column.Type.Type);
            column = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable, column.DefaultLiteral, column.Collation,
                column.DomainName, column.DefaultFunctionCalls, column.Identity, missing);
        }

        var columns = table.Columns.Append(column).ToArray();
        int primaryOrdinal = -1;
        for (int ordinal = 0; ordinal < plan.Constraints.Count; ordinal++)
        {
            if (plan.Constraints[ordinal].Kind == SqlConstraintKind.PrimaryKey)
            {
                primaryOrdinal = ordinal;
                break;
            }
        }
        var primaryDefinition = primaryOrdinal < 0 ? null : plan.Constraints[primaryOrdinal];
        if (primaryDefinition is not null && table.PrimaryKeyColumns.Count > 0)
        {
            throw SqlConstraintDeclaration.MultiplePrimaryKeys(table.Name, ConstraintName(table, primaryDefinition, primaryOrdinal));
        }

        var primary = primaryDefinition?.Columns ?? table.PrimaryKeyColumns;
        // The added column takes the next physical ordinal: the replacement keeps the
        // table's dropped ordinals, so every existing column keeps its own (the catalog
        // refuses a replacement that does not).
        var provisional = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, primary, table.Owner, table.OwningSchema,
            table.Constraints, table.DroppedColumnOrdinals);
        var parents = new SortedDictionary<ulong, SqlCatalogTable>();
        var constraints = BindConstraints(provisional, plan.Constraints, parents);
        await LockReferencedTablesAsync(parents, statement, cancellationToken).ConfigureAwait(false);
        constraints = BindConstraints(provisional, plan.Constraints);
        var replacement = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, primary, table.Owner, table.OwningSchema,
            table.Constraints.Concat(constraints).ToArray(), table.DroppedColumnOrdinals);
        _definitions.GetDeclared(replacement, [.. _definitions.Get(table, cancellationToken).DefaultValues, boundDefault], cancellationToken);
        if (plan.Column.IsNullable && plan.Constraints.Count == 0)
        {
            // Existing columns and constraints are unchanged. A nullable field's missing
            // value is one immutable literal, so validate it once instead of scanning rows.
            var definition = _definitions.Get(replacement, cancellationToken);
            var row = new object?[columns.Length];
            row[^1] = definition.MissingValues[^1] is { } literal ? DefaultValue(literal, cancellationToken) : null;
            try
            {
                CheckDomains(definition, row, ExecutionEvaluator(null, cancellationToken, transactionTimestamp: statement.TransactionStart), [columns.Length - 1]);
            }
            catch (SqlConstraintViolationException)
            {
                // An empty table assigns no missing value. Keep that DDL legal even
                // when all retained versions are tombstoned. Valid defaults never scan.
                if (Scan(table, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Any())
                {
                    throw;
                }
            }
            _definitions.Adopt(await SelfCommitAsync(statement,
                (statement.ScriptReceipt is null ? _catalog.AddColumnAsync(plan.Schema, plan.Name, column, cancellationToken) : _catalog.AddColumnAsync(plan.Schema, plan.Name, column, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false), replacement, CancellationToken.None);
            return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
        }
        // Backfill is logical: missing trailing fields resolve from the immutable
        // replacement metadata. Existing row bytes and MVCC stamps never change;
        // one durable catalog publication makes the complete addition visible.
        var snapshot = ConstraintCurrentSnapshot(statement);
        long total = statement.ProvisioningProgress is null ? 0 : Scan(replacement, statement, cancellationToken, snapshot).LongCount();
        var bound = plan.Column.DomainName is null ? null : _definitions.Get(replacement, cancellationToken);
        var evaluator = bound is null ? null : ExecutionEvaluator(null, cancellationToken, transactionTimestamp: statement.TransactionStart);

        // Each existing row is assigned the new column's default, or NULL, of its domain (phase E3).
        int[]? added = bound is null ? null : [columns.Length - 1];
        long processed = 0;
        statement.ProvisioningProgress?.Invoke(0, total);
        foreach (var (_, row) in Scan(replacement, statement, cancellationToken, snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            row[^1] = CoerceForColumn(row[^1], column);
            if (added is not null)
            {
                CheckDomains(bound!, row, evaluator!, added);
            }
            ValidateRows(replacement, [row], statement, cancellationToken, current: true);
            statement.ProvisioningProgress?.Invoke(++processed, total);
        }

        if (plan.Constraints.Count == 0)
        {
            // The catalog publishes its own copy of the replacement definition, built from the
            // same column and constraint instances, so it adopts the replacement's bindings.
            _definitions.Adopt(await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.AddColumnAsync(plan.Schema, plan.Name, column, cancellationToken) : _catalog.AddColumnAsync(plan.Schema, plan.Name, column, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false),
                replacement, CancellationToken.None);
        }
        else
        {
            await PublishConstrainedTableAsync(replacement, plan.Constraints, statement, cancellationToken, replaceExisting: true).ConfigureAwait(false);
        }
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private async Task<DatabaseResult> ExecuteDropConstraintAsync(SqlDropConstraintPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE DROP CONSTRAINT", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        if (plan.Table.Constraints.Any(c => string.Equals(c.Name, plan.ConstraintName, StringComparison.OrdinalIgnoreCase)))
        {
            // The new version no longer carries the constraint, so the dropped predicate is
            // never evaluated again; it keeps the other bindings of the version it came from.
            _definitions.Adopt(await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.DropConstraintAsync(plan.Table.Schema, plan.Table.Name, plan.ConstraintName, cancellationToken) : _catalog.DropConstraintAsync(plan.Table.Schema, plan.Table.Name, plan.ConstraintName, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false), plan.Table, CancellationToken.None);
        }
        else if (_catalog.TryGetIndex(plan.Table.ObjectId, plan.ConstraintName, out var index) && index.IsUnique)
        {
            return await ExecuteDropIndexAsync(new SqlDropIndexPlan(plan.Table, plan.ConstraintName, false), statement, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new SqlExecutionException(SqlExecutionException.UndefinedObjectCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutionConstraintMissing, plan.ConstraintName, plan.Table.Name));
        }

        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private void EnsureCanDropIndex(SqlCatalogTable table, SqlCatalogIndex index)
    {
        if (index.IsUnique && (index.IsPrimaryKey ||
            IncomingReferences(table).Any(reference => SameConstraintColumns(reference.Constraint.ReferencedColumns!, index.ColumnNames))))
        {
            throw new SqlExecutionException(SqlExecutionException.DependentObjectsCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ConstraintIndexDependent, index.Name));
        }
    }

    /// <summary>
    /// Refuses to drop a column a constraint uses, naming the constraint to drop first, with
    /// the catalog's wording. A missing column and a primary-key column are left to the
    /// catalog's own refusals, which name them as such.
    /// </summary>
    private void EnsureCanDropColumn(SqlCatalogTable table, string columnName, CancellationToken cancellationToken)
    {
        int ordinal = -1;
        for (int index = 0; index < table.Columns.Count; index++)
        {
            if (string.Equals(table.Columns[index].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = index;
                break;
            }
        }

        if (ordinal < 0 || table.PrimaryKeyColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var constraint in table.Constraints)
        {
            if (constraint.Columns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                throw UsedByConstraint(constraint);
            }
        }

        foreach (var reference in IncomingReferences(table))
        {
            if (reference.Constraint.ReferencedColumns!.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                throw UsedByConstraint(reference.Constraint);
            }
        }

        // A table-level CHECK lists no columns; its bound predicate records every column it
        // reads. Binding the predicate without the column (binding only, as at load, so a
        // declaration rule tightened since the check was stored cannot block an unrelated
        // drop) backs that up, and either way the refusal names the CHECK, not the binder's
        // unknown column.
        var columns = table.Columns.Where((_, index) => index != ordinal).ToArray();
        foreach (var check in _definitions.Get(table, cancellationToken).Checks)
        {
            if (check.ColumnOrdinals.Contains(ordinal))
            {
                throw UsedByConstraint(check.Constraint);
            }

            // A predicate bound as unresolved (a function the engine does not register) cannot bind
            // again either; its columns, all it needs here, are its ordinals above.
            if (check.Unresolved is not null)
            {
                continue;
            }

            try
            {
                _ = BindPersistedCheck(check.Predicate, new SqlCatalogTable(0, table.Schema, table.Name, columns),
                    _catalog.DefaultCollation, _definitions.Functions, check.Constraint.CheckFunctionCalls, cancellationToken);
            }
            catch (DatabaseException exception)
            {
                throw UsedByConstraint(check.Constraint, exception);
            }
        }

        DatabaseException UsedByConstraint(SqlCatalogConstraint constraint, Exception? cause = null)
            => new(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogDropColumnCoreAsyncSqlCatalog2, columnName, constraint.Name), cause);
    }
}
