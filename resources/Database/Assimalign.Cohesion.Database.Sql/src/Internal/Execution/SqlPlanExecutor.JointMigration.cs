using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Metadata only. This runs in Build/open: never enumerate stored rows here.
    internal SqlJointMigrationPlan PlanJointMigration(SqlCompiledSchema schema, SqlMigrationScript script,
        CancellationToken cancellationToken)
    {
        var names = script.Plan.Operations.Where(operation => operation.Kind is not (SqlSchemaMigrationOperationKind.AddDomain or SqlSchemaMigrationOperationKind.DropDomain))
            .Select(operation => operation.ParentName ?? operation.ObjectName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsTargetName(string schemaName, string name)
            => schemaName.Equals(SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase) && names.Contains(name);
        bool IsTarget(SqlCatalogTable table) => IsTargetName(table.Schema, table.Name);
        var image = _catalog.CaptureSnapshot();
        var planner = new SqlPlanner(_catalog, null, _definitions.Functions, cancellationToken);
        var plans = schema.Tables.Where(table => names.Contains(table.Name)).Select(table =>
            (Declaration: table, Plan: (SqlCreateTablePlan)planner.Plan(
                SqlQueryRequest.FromSql(SqlSchemaStatementRenderer.CreateTable(table), null,
                    SqlQueryRequest.CeilingParserOptions).Statement.SqlExpression))).ToArray();
        var shapes = plans.Select((pair, index) => new SqlCatalogTable(
            image.TryGetTable(SqlPlanner.DefaultSchema, pair.Plan.Name, out var previous) ? previous.ObjectId : (ulong)long.MaxValue - (ulong)index,
            pair.Plan.Schema, pair.Plan.Name, pair.Plan.Columns, pair.Plan.PrimaryKey,
            DatabaseObjectOwner.Schema, schema.Name)).ToArray();
        var dropped = image.Tables.Where(table => IsTarget(table) &&
            !schema.Tables.Any(target => target.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var table in dropped) { EnsureNoViewReads(table); }
        var all = image.Tables.Where(table => !IsTarget(table)).Concat(shapes).ToArray();
        var indexes = new List<SqlCatalogIndex>();
        for (int i = 0; i < shapes.Length; i++)
        {
            var table = shapes[i];
            if (table.PrimaryKeyColumns.Count != 0)
            { indexes.Add(new SqlCatalogIndex(table.ObjectId, $"PrimaryKey_{table.Name}", table.PrimaryKeyColumns, true, table.Owner, table.OwningSchema, isPrimaryKey: true)); }
            foreach (var index in plans[i].Declaration.Indexes)
            {
                var predicate = index.Predicate is null ? null : SqlPartialIndexPredicate.Declare(table, index.Name, index.Columns,
                    index.IsUnique, SqlPersistedExpression.ParseDeclaration(index.Predicate, SqlQueryRequest.CeilingParserOptions),
                    _catalog.DefaultCollation, _definitions.Functions);
                indexes.Add(new SqlCatalogIndex(table.ObjectId, index.Name, index.Columns, index.IsUnique, table.Owner, table.OwningSchema,
                    predicate: predicate?.Predicate, predicateColumns: predicate?.PredicateColumns,
                    predicateFunctionCalls: predicate?.PredicateFunctionCalls,
                    keyOrders: index.KeyOrders.Select(order => (SqlCatalogIndexKeyOrder)order).ToArray()));
            }
            // Ad-hoc indexes are outside the typed document. Preserve them, or refuse
            // their invalidated dependency before reserving any physical work.
            if (image.TryGetTable(table.Schema, table.Name, out var old))
            {
                foreach (var index in image.GetIndexes(old.ObjectId).Where(index => index.Owner != DatabaseObjectOwner.Schema))
                {
                    foreach (var column in index.ColumnNames.Concat(index.PredicateColumns))
                    {
                        if (table.FindColumn(column) is null)
                        { throw new DatabaseObjectLockedException(column, schema.Name, "ALTER TABLE DROP COLUMN"); }
                        if (index.PredicateColumns.Contains(column, StringComparer.OrdinalIgnoreCase) &&
                            old.FindColumn(column)!.Type.Type != table.FindColumn(column)!.Type.Type)
                        { throw new SqlPlanningException("CS03083", string.Format(CultureInfo.CurrentCulture, ErrorMessages.PartialPredicateTypeChange, column, index.Name)); }
                    }
                    indexes.Add(index);
                }
            }
        }
        var allIndexes = image.Tables.Where(table => !IsTarget(table)).SelectMany(table => image.GetIndexes(table.ObjectId))
            .Concat(indexes).ToArray();
        var result = new List<SqlJointMigrationTable>();
        for (int i = 0; i < shapes.Length; i++)
        {
            var provisional = shapes[i];
            image.TryGetTable(provisional.Schema, provisional.Name, out var old);
            var constraints = BindConstraints(provisional, plans[i].Plan.Constraints, preparedTables: all, preparedIndexes: allIndexes);
            var table = new SqlCatalogTable(provisional.ObjectId, provisional.Schema, provisional.Name, provisional.Columns,
                provisional.PrimaryKeyColumns, provisional.Owner, provisional.OwningSchema, constraints);
            var values = new SqlBoundExpression?[table.Columns.Count];
            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                var column = table.Columns[ordinal];
                if (old?.FindColumn(column.Name) is { } previous)
                {
                    bool changed = previous.Type.Type != column.Type.Type || previous.Type.MaxLength != column.Type.MaxLength ||
                        previous.Type.Precision != column.Type.Precision || previous.Type.Scale != column.Type.Scale ||
                        !string.Equals(previous.DomainName, column.DomainName, StringComparison.OrdinalIgnoreCase) || previous.Collation != column.Collation;
                    if (changed) { EnsureNoViewReads(old, column.Name, rename: true); }
                    values[ordinal] = changed ? planner.PlanMigrationConversion(old, column) : new SqlBoundColumn(FindColumnOrdinal(old, column.Name));
                }
                else if (column.DefaultLiteral is not null)
                { values[ordinal] = BindDefaultExpression(column, "ADD COLUMN DEFAULT", _definitions.Functions, _catalog.DefaultCollation, cancellationToken, declaring: true); }
            }
            if (old is not null)
            {
                foreach (var column in old.Columns.Where(column => table.FindColumn(column.Name) is null))
                { EnsureNoViewReads(old, column.Name); }
            }
            var bound = _definitions.Get(table, cancellationToken);
            var evaluator = ExecutionEvaluator(null, cancellationToken);
            for (int ordinal = 0; ordinal < table.Columns.Count; ordinal++)
            {
                if (bound.DefaultValues[ordinal] is SqlBoundConstant constant && bound.DomainAt(ordinal) is { } domain)
                {
                    var row = new object?[table.Columns.Count];
                    row[ordinal] = constant.Value;
                    CheckDomain(table, domain, row, evaluator);
                }
            }
            result.Add(new SqlJointMigrationTable(old, table, indexes.Where(index => index.TableObjectId == table.ObjectId).ToArray(), values));
        }
        // Include both ends of old and new FKs, even when the old parent is dropped
        // or a constraint is re-pointed. Stable adjacent tables keep their owners.
        var touched = image.Tables.Where(IsTarget).ToDictionary(table => table.ObjectId);
        foreach (var child in image.Tables.Concat(result.Select(table => table.Shape)))
        {
            foreach (var reference in child.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference))
            {
                if (!IsTarget(child) && !IsTargetName(reference.ReferencedSchema!, reference.ReferencedTable!)) { continue; }
                if (image.TryGetTable(child.Schema, child.Name, out var liveChild)) { touched.TryAdd(liveChild.ObjectId, liveChild); }
                if (image.TryGetTable(reference.ReferencedSchema!, reference.ReferencedTable!, out var parent)) { touched.TryAdd(parent.ObjectId, parent); }
            }
        }
        // Validate incoming declarations against the final types/keys too.
        foreach (var child in all.Where(table => !IsTarget(table)))
        {
            foreach (var reference in child.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference && IsTargetName(constraint.ReferencedSchema!, constraint.ReferencedTable!)))
            {
                var parent = all.FirstOrDefault(table => table.Schema.Equals(reference.ReferencedSchema, StringComparison.OrdinalIgnoreCase) && table.Name.Equals(reference.ReferencedTable, StringComparison.OrdinalIgnoreCase));
                if (parent is null) { throw new SqlExecutionException(SqlExecutionException.DependentObjectsCode, string.Format(CultureInfo.CurrentCulture, ErrorMessages.TableForeignKeyDependent, reference.ReferencedTable)); }
                _ = new SqlBoundReference(child, reference, parent, _definitions);
                for (int key = 0; key < reference.Columns.Count; key++)
                {
                    if (child.FindColumn(reference.Columns[key])!.Type.Type != parent.FindColumn(reference.ReferencedColumns![key])!.Type.Type)
                    { throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyColumnTypes, reference.Name)); }
                    if (child.FindColumn(reference.Columns[key])!.Type.Type is DatabaseType.String or DatabaseType.Character &&
                        (child.FindColumn(reference.Columns[key])!.Collation ?? _catalog.DefaultCollation) !=
                        (parent.FindColumn(reference.ReferencedColumns![key])!.Collation ?? _catalog.DefaultCollation))
                    { throw new SqlExecutionException(SqlExecutionException.CollationMismatchCode, string.Format(CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyColumnCollations, reference.Name)); }
                }
                if (!allIndexes.Any(index => index.TableObjectId == parent.ObjectId && index.IsUnique && index.Predicate is null &&
                    SameConstraintColumns(index.ColumnNames, reference.ReferencedColumns!)))
                { throw new SqlExecutionException(SqlExecutionException.InvalidForeignKeyCode, string.Format(CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyUniqueRequired, reference.Name)); }
            }
        }
        return new SqlJointMigrationPlan(result.ToArray(), dropped, touched.Values.OrderBy(table => table.ObjectId).ToArray());
    }

    internal async Task ExecuteJointMigrationAsync(SqlJointMigrationPlan plan, SqlCompiledSchema schema,
        SqlStatementContext statement, Action<string> stage, CancellationToken cancellationToken)
    {
        var changed = plan.Tables.Where(table => table.Previous is not null).ToArray();
        var previous = changed.Select(table => table.Previous!).ToArray();
        var prepared = new List<SqlCatalogTable>(plan.Tables.Length);
        var created = new List<SqlCatalogIndex>();
        var retiring = previous.Concat(plan.Dropped).ToArray();
        var oldIndexes = retiring.SelectMany(table => _catalog.GetIndexes(table.ObjectId)).ToArray();
        var retiredIndexes = new Dictionary<SqlCatalogIndex, BTreeIndex>(ReferenceEqualityComparer.Instance);
        foreach (var index in oldIndexes)
        { if (_indexManager.TryGetIndex(index.TableObjectId, index.Name, out var tree)) { retiredIndexes.Add(index, tree); } }
        bool published = false;
        bool unconfirmed = false;
        var failures = new List<Exception>(3);
        try
        {
            _catalog.ReserveStorageReplacement(previous, null, (table, id) =>
            {
                var shape = changed.First(item => item.Previous!.ObjectId == table.ObjectId).Shape;
                var replacement = WithOwner(shape, id);
                prepared.Add(replacement);
                return replacement;
            }, cancellationToken);
            foreach (var item in plan.Tables.Where(table => table.Previous is null))
            {
                _catalog.ReserveStorageReplacement([WithOwner(item.Shape, 0)], null, (_, id) =>
                {
                    var replacement = WithOwner(item.Shape, id);
                    prepared.Add(replacement);
                    return replacement;
                }, cancellationToken, newTable: true);
            }
            stage("OwnersDurable");
            var descriptions = prepared.SelectMany(table => plan.Tables.Single(item => item.Shape.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase)).Indexes
                .Select(index => new SqlCatalogIndex(table.ObjectId, index.Name, index.ColumnNames, index.IsUnique, index.Owner, index.OwningSchema,
                    index.IsPrimaryKey, index.Predicate, index.PredicateColumns, index.PredicateFunctionCalls, index.KeyOrders))).ToArray();
            created.EnsureCapacity(descriptions.Length);
            foreach (var table in prepared)
            {
                await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner, LockResource.Object(table.ObjectId),
                    LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                _definitions.Get(table, cancellationToken);
            }
            statement.CheckCopySpace(descriptions.Length);
            long journalStart = 0;
            await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async _ =>
            {
                statement.CheckCopySpace(descriptions.Length);
                journalStart = _storage.Journal.Length - 38; // Include this bracket's already appended Begin frame.
                foreach (var index in descriptions)
                {
                    await _indexManager.CreateIndexAsync(statement.Transaction, index.TableObjectId,
                        new IndexDefinition(index.Name, IndexKind.BTree, index.IsUnique), cancellationToken).ConfigureAwait(false);
                    created.Add(index);
                }
                return true;
            }, durable: true, cancellationToken).ConfigureAwait(false);
            await CheckpointReplacementCopyAsync(statement, journalStart, cancellationToken).ConfigureAwait(false);
            stage("TreesDurable");
            foreach (var item in changed)
            {
                var table = prepared.Single(table => table.Name.Equals(item.Shape.Name, StringComparison.OrdinalIgnoreCase));
                var bound = _definitions.Get(table, cancellationToken);
                var evaluator = ExecutionEvaluator(null, cancellationToken, transactionTimestamp: statement.TransactionStart);
                await CopyReplacementRowsAsync(item.Previous!, table, descriptions.Where(index => index.TableObjectId == table.ObjectId).ToArray(), statement, values =>
                {
                    var converted = new object?[table.Columns.Count];
                    for (int ordinal = 0; ordinal < converted.Length; ordinal++)
                    { converted[ordinal] = CoerceForColumn(item.Values[ordinal] is { } expression ? evaluator.Evaluate(expression, values) : null, table.Columns[ordinal]); }
                    CheckDomains(bound, converted, evaluator);
                    ValidateRows(table, [converted], statement, cancellationToken, checksOnly: true);
                    return converted;
                }, cancellationToken, item.CopyRows).ConfigureAwait(false);
                stage("TablePrepared");
            }
            var replacedNames = retiring.Select(table => (table.Schema, table.Name)).ToHashSet();
            var final = _catalog.Tables.Where(table => !replacedNames.Contains((table.Schema, table.Name))).Concat(prepared).ToArray();
            var snapshot = ConstraintCurrentSnapshot(statement);
            foreach (var child in final)
            {
                foreach (var constraint in child.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference))
                {
                    var parent = final.Single(table => table.Schema.Equals(constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase) && table.Name.Equals(constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase));
                    if (!prepared.Contains(child) && !prepared.Contains(parent)) { continue; }
                    var reference = BindReference(child, constraint, parent, statement, cancellationToken);
                    if (prepared.Contains(parent)) { ConstraintLookup(reference.ParentKey, statement, descriptions.Where(index => index.TableObjectId == parent.ObjectId).ToArray()); }
                    foreach (var row in Scan(child, statement, cancellationToken, snapshot))
                    {
                        var key = reference.ChildKey.Read(row.Values);
                        if (key.Any(value => value is null)) { continue; }
                        if (!FindConstraintRows(reference.ParentKey, key, statement, cancellationToken, snapshot).Any())
                        { throw Violation(child, constraint, key); }
                    }
                }
            }
            stage("Validated");
            await _catalog.TruncateSnapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TransactionContext? marker = null;
            try
            {
                marker = await statement.Coordinator.BeginSnapshotPinAsync(cancellationToken).ConfigureAwait(false);
                stage("Publishing");
                try
                {
                    await _catalog.PublishStorageReplacementAsync(previous, null, prepared, descriptions, PublishedRegistrations(descriptions),
                        retiredIndexes, false, marker.Sequence, string.Empty, null, cancellationToken, definitionChange: true,
                        appliedSchema: new SqlCatalogSchemaState(schema.Hash, schema.CanonicalDocument, 0), dropped: plan.Dropped).ConfigureAwait(false);
                }
                catch (Exception failure) when (SqlSchemaProvisioner.IsCommitUnconfirmed(failure))
                {
                    throw new SqlCatalogException(SqlProvisioningCodes.JointUnconfirmed, ErrorMessages.JointPublicationUnconfirmed, failure);
                }
                published = true;
                statement.Metrics.RecordSelfCommit();
            }
            finally
            {
                try
                {
                    if (marker is not null)
                    {
                        await statement.Coordinator.RollbackAsync(marker, CancellationToken.None).ConfigureAwait(false);
                        if (published) { _catalog.ReleaseTruncatePublication(retiring); }
                    }
                }
                finally { _catalog.TruncateSnapshotGate.Release(); }
            }
            stage("Published");
        }
        catch (Exception failure)
        {
            unconfirmed = SqlSchemaProvisioner.IsJointUnconfirmed(failure);
            failures.Add(failure);
            throw;
        }
        finally
        {
            // Keep the original preparation error if either mandatory cleanup fails.
            // Capacity was reserved before work; abandonment itself allocates nothing.
            int before = failures.Count;
            try { if (!unconfirmed) { await RemoveDroppedIndexesAsync(statement, published ? oldIndexes : created, cancellationToken: CancellationToken.None).ConfigureAwait(false); } }
            catch (Exception cleanup) { failures.Add(cleanup); }
            if (!published && !unconfirmed)
            {
                try { await _catalog.DiscardJointIntentAsync(CancellationToken.None, prepared).ConfigureAwait(false); }
                catch (Exception cleanup) { failures.Add(cleanup); }
                finally { _catalog.AbandonStorageReplacement(prepared); }
            }
            if (failures.Count != before) { throw new AggregateException(failures); }
        }
    }

    private static SqlCatalogTable WithOwner(SqlCatalogTable shape, ulong id)
        => new(id, shape.Schema, shape.Name, shape.Columns, shape.PrimaryKeyColumns, shape.Owner, shape.OwningSchema, shape.Constraints);
}
