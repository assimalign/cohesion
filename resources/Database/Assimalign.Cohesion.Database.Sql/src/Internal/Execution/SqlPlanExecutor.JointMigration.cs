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
        var registrations = _indexManager.ExportRegistrations();
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
        var scripted = _catalog.Scripts.SelectMany(receipt => receipt.Objects).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < shapes.Length; i++)
        {
            var table = shapes[i];
            if (table.PrimaryKeyColumns.Count != 0)
            {
                // The marker and columns identify the key tree. Inherit its generated
                // name so both kept heaps and rewrites retain the statement-visible name.
                var stored = image.TryGetTable(table.Schema, table.Name, out var keyed)
                    ? image.GetIndexes(keyed.ObjectId).FirstOrDefault(index => index.IsPrimaryKey &&
                        index.ColumnNames.SequenceEqual(table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase))
                    : null;
                var declared = plans[i].Plan.Constraints.Select((definition, ordinal) => (definition, ordinal))
                    .FirstOrDefault(item => item.definition.Kind == SqlConstraintKind.PrimaryKey);
                string name = stored?.Name ?? (declared.definition is not null
                    ? ConstraintName(table, declared.definition, declared.ordinal) : $"PrimaryKey_{table.Name}");
                indexes.Add(new SqlCatalogIndex(table.ObjectId, name, table.PrimaryKeyColumns, true,
                    table.Owner, table.OwningSchema, isPrimaryKey: true));
            }
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
            // Ad-hoc and script indexes are outside the typed document. Preserve them, or refuse
            // their invalidated dependency before reserving any physical work.
            if (image.TryGetTable(table.Schema, table.Name, out var old))
            {
                foreach (var index in image.GetIndexes(old.ObjectId).Where(index => index.Owner != DatabaseObjectOwner.Schema ||
                    scripted.Contains(SqlObjectIdentity.Index(old.Schema, old.Name, index.Name)) &&
                    !plans[i].Declaration.Indexes.Any(declared => declared.Name.Equals(index.Name, StringComparison.OrdinalIgnoreCase)) &&
                    !script.Plan.Operations.Any(operation => operation.Kind == SqlSchemaMigrationOperationKind.DropIndex &&
                        string.Equals(operation.ParentName, old.Name, StringComparison.OrdinalIgnoreCase) &&
                        operation.ObjectName.Equals(index.Name, StringComparison.OrdinalIgnoreCase))))
                {
                    foreach (var column in index.ColumnNames.Concat(index.PredicateColumns))
                    {
                        if (table.FindColumn(column) is null)
                        { throw new SqlCatalogException("CS03056", string.Format(CultureInfo.CurrentCulture,
                            ErrorMessages.CatalogDropColumnCoreAsyncSqlCatalog3, column, index.Name, old.Schema, old.Name)); }
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
            var tableIndexes = indexes.Where(index => index.TableObjectId == table.ObjectId).ToArray();
            bool keepsOwner = old is not null && old.Columns.Count == table.Columns.Count &&
                table.Columns.Select((column, ordinal) =>
                    column.Name.Equals(old.Columns[ordinal].Name, StringComparison.OrdinalIgnoreCase) &&
                    values[ordinal] is SqlBoundColumn identity && identity.Ordinal == ordinal).All(identity => identity) &&
                image.GetIndexes(old.ObjectId).Where(index => !SameJointIndex(index, tableIndexes)).All(index =>
                    !registrations.Any(registration => registration.ObjectId == old.ObjectId &&
                        registration.Definition.Name.Equals(index.Name, StringComparison.OrdinalIgnoreCase) &&
                        _indexManager.PageOwner(registration) == old.ObjectId)) &&
                script.Plan.Operations.Where(operation =>
                string.Equals(operation.ParentName ?? operation.ObjectName, table.Name, StringComparison.OrdinalIgnoreCase)).All(operation =>
                    operation.Kind is SqlSchemaMigrationOperationKind.AddIndex or SqlSchemaMigrationOperationKind.DropIndex or
                        SqlSchemaMigrationOperationKind.AddConstraint or SqlSchemaMigrationOperationKind.DropConstraint ||
                    operation.Kind == SqlSchemaMigrationOperationKind.AlterColumn && operation.PreviousColumn is { } column &&
                        column with { IsNullable = operation.Column!.IsNullable } == operation.Column);
            if (keepsOwner)
            {
                // Frozen missing tails and physical ordinals belong to the kept heap.
                var columns = table.Columns.Select((column, ordinal) => new SqlCatalogColumn(column.Name, old!.Columns[ordinal].Type,
                    column.IsNullable, old.Columns[ordinal].DefaultLiteral, old.Columns[ordinal].Collation, old.Columns[ordinal].DomainName,
                    old.Columns[ordinal].DefaultFunctionCalls, old.Columns[ordinal].Identity, old.Columns[ordinal].MissingValueLiteral)
                    { RecastDefault = old.Columns[ordinal].RecastDefault }).ToArray();
                table = new SqlCatalogTable(old!.ObjectId, table.Schema, table.Name, columns, table.PrimaryKeyColumns,
                    table.Owner, table.OwningSchema, table.Constraints, old.DroppedColumnOrdinals);
            }
            if (keepsOwner)
            {
                // Retain the registered spelling as well as its tree. SQL names
                // compare without case, while the physical manager keys are exact.
                var previousIndexes = image.GetIndexes(old!.ObjectId);
                tableIndexes = tableIndexes.Select(index => previousIndexes.FirstOrDefault(previous => SameJointIndex(index, previous)) ?? index).ToArray();
            }
            result.Add(new SqlJointMigrationTable(old, table, tableIndexes, values, keepsOwner));
        }
        bool KeepsReference(SqlCatalogTable child, SqlCatalogConstraint reference)
        {
            var childPlan = result.FirstOrDefault(item => item.Shape.Schema.Equals(child.Schema, StringComparison.OrdinalIgnoreCase) &&
                item.Shape.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase));
            var parentPlan = result.FirstOrDefault(item => item.Shape.Schema.Equals(reference.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                item.Shape.Name.Equals(reference.ReferencedTable, StringComparison.OrdinalIgnoreCase));
            if (IsTarget(child) && childPlan?.KeepsOwner != true ||
                IsTargetName(reference.ReferencedSchema!, reference.ReferencedTable!) && parentPlan?.KeepsOwner != true) { return false; }
            if (!image.TryGetTable(child.Schema, child.Name, out var oldChild) ||
                !oldChild.Constraints.Any(old => SameJointConstraint(reference, old))) { return false; }
            var finalChild = childPlan?.Shape ?? oldChild;
            var finalParent = parentPlan?.Shape ?? all.FirstOrDefault(table =>
                table.Schema.Equals(reference.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                table.Name.Equals(reference.ReferencedTable, StringComparison.OrdinalIgnoreCase));
            return finalParent is not null && finalChild.Constraints.Any(current => SameJointConstraint(reference, current)) &&
                !NeedsJointReferenceValidation(finalChild, reference, finalParent, indexes);
        }
        // Plan targets always reserve X. An omitted neighbour has the identical FK,
        // kept identities at both ends and unchanged matching parent key trees, so
        // every writable row still enforces the old/new constraint. FK probes acquire
        // adjacent IS before selecting a lookup tree (Constraints.cs); a wait releases
        // new grants and rediscovers incoming bindings/parents, and BindReference gives
        // changed descriptions new lookup keys. No retired handle survives that retry.
        var touched = image.Tables.Where(IsTarget).ToDictionary(table => table.ObjectId);
        foreach (var child in image.Tables.Concat(result.Select(table => table.Shape)))
        {
            foreach (var reference in child.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference))
            {
                if (!IsTarget(child) && !IsTargetName(reference.ReferencedSchema!, reference.ReferencedTable!)) { continue; }
                if (KeepsReference(child, reference)) { continue; }
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
        var changed = plan.Tables.Where(table => table.Previous is not null && !table.KeepsOwner).ToArray();
        var keeping = plan.Tables.Where(table => table.KeepsOwner).ToArray();
        var previous = changed.Select(table => table.Previous!).ToArray();
        var prepared = new List<SqlCatalogTable>(plan.Tables.Length);
        var created = new List<SqlCatalogIndex>();
        var reservedTrees = new List<ulong>(keeping.Sum(item => item.Indexes.Length));
        var retiring = previous.Concat(plan.Dropped).ToArray();
        var oldIndexes = retiring.SelectMany(table => _catalog.GetIndexes(table.ObjectId)).Concat(keeping.SelectMany(item =>
            _catalog.GetIndexes(item.Previous!.ObjectId).Where(index => !item.Indexes.Any(current => current.Name.Equals(index.Name, StringComparison.OrdinalIgnoreCase))))).ToArray();
        var retiredIndexes = new Dictionary<SqlCatalogIndex, BTreeIndex>(ReferenceEqualityComparer.Instance);
        foreach (var index in oldIndexes.Where(index => retiring.Any(table => table.ObjectId == index.TableObjectId)))
        { if (_indexManager.TryGetIndex(index.TableObjectId, index.Name, out var tree)) { retiredIndexes.Add(index, tree); } }
        bool published = false;
        bool unconfirmed = false;
        var failures = new List<Exception>(3);
        try
        {
            // Validate new row constraints before allocating or building any trees.
            // Identity columns need no conversion or scan for an index-only change.
            foreach (var item in keeping.Where(item => NeedsJointRowValidation(item.Previous!, item.Shape)))
            {
                long processed = 0;
                statement.ProvisioningProgress?.Invoke(0, null);
                foreach (var row in Scan(item.Shape, statement, cancellationToken, ConstraintCurrentSnapshot(statement)))
                {
                    for (int ordinal = 0; ordinal < row.Values.Length; ordinal++)
                    {
                        if (item.Previous!.Columns[ordinal].IsNullable && !item.Shape.Columns[ordinal].IsNullable)
                        { _ = CoerceForColumn(row.Values[ordinal], item.Shape.Columns[ordinal]); }
                    }
                    ValidateRows(item.Shape, [row.Values], statement, cancellationToken, checksOnly: true);
                    statement.ProvisioningProgress?.Invoke(++processed, null);
                }
                statement.ProvisioningProgress?.Invoke(processed, processed);
            }
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
            var descriptions = prepared.Concat(keeping.Select(item => item.Shape)).SelectMany(table => plan.Tables.Single(item => item.Shape.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase)).Indexes
                .Select(index => new SqlCatalogIndex(table.ObjectId, index.Name, index.ColumnNames, index.IsUnique, index.Owner, index.OwningSchema,
                    index.IsPrimaryKey, index.Predicate, index.PredicateColumns, index.PredicateFunctionCalls, index.KeyOrders))).ToArray();
            var roots = descriptions.Where(index => prepared.Any(table => table.ObjectId == index.TableObjectId)).ToArray();
            created.EnsureCapacity(roots.Length);
            foreach (var table in prepared)
            {
                await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner, LockResource.Object(table.ObjectId),
                    LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                _definitions.Get(table, cancellationToken);
            }
            statement.CheckCopySpace(roots.Length);
            long journalStart = 0;
            await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async _ =>
            {
                statement.CheckCopySpace(roots.Length);
                long allocationStart = statement.CaptureCopyAllocations();
                journalStart = _storage.JournalLength - 38; // Include this bracket's already appended Begin frame.
                foreach (var index in roots)
                {
                    await _indexManager.CreateIndexAsync(statement.Transaction, index.TableObjectId,
                        new IndexDefinition(index.Name, IndexKind.BTree, index.IsUnique), cancellationToken).ConfigureAwait(false);
                    created.Add(index);
                }
                statement.AddCopyAllocations(allocationStart);
                return true;
            }, durable: true, cancellationToken).ConfigureAwait(false);
            await CheckpointReplacementCopyAsync(statement, journalStart, cancellationToken).ConfigureAwait(false);
            stage("TreesDurable");
            foreach (var item in keeping)
            {
                foreach (var index in item.Indexes.Where(index => !SameJointIndex(index, _catalog.GetIndexes(item.Previous!.ObjectId))))
                {
                    ulong owner = _catalog.ReserveJointTreeOwner(item.Shape, cancellationToken);
                    reservedTrees.Add(owner);
                    stage("BeforeTreeBuild");
                    await BuildDeferredIndexAsync(item.Shape, new IndexDefinition(index.Name, IndexKind.BTree, index.IsUnique),
                        index.ColumnNames.Select(name => FindColumnOrdinal(item.Shape, name)).ToArray(), statement,
                        () => { }, cancellationToken, index, owner).ConfigureAwait(false);
                    stage("AfterTreeBuild");
                }
            }
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
                }, cancellationToken).ConfigureAwait(false);
                stage("TablePrepared");
            }
            var preparedAll = prepared.Concat(keeping.Select(item => item.Shape)).ToArray();
            var replacedNames = retiring.Concat(keeping.Select(item => item.Previous!)).Select(table => (table.Schema, table.Name)).ToHashSet();
            var final = _catalog.Tables.Where(table => !replacedNames.Contains((table.Schema, table.Name))).Concat(preparedAll).ToArray();
            var snapshot = ConstraintCurrentSnapshot(statement);
            foreach (var child in final)
            {
                foreach (var constraint in child.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference))
                {
                    var parent = final.Single(table => table.Schema.Equals(constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase) && table.Name.Equals(constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase));
                    if (!NeedsJointReferenceValidation(child, constraint, parent, descriptions)) { continue; }
                    var reference = BindReference(child, constraint, parent, statement, cancellationToken);
                    if (preparedAll.Contains(parent)) { ConstraintLookup(reference.ParentKey, statement, descriptions.Where(index => index.TableObjectId == parent.ObjectId).ToArray()); }
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
                _indexManager.PreparePublication();
                try
                {
                    await _catalog.PublishStorageReplacementAsync(previous, null, prepared, descriptions, PublishedRegistrations(descriptions, includePrepared: true),
                        retiredIndexes, false, marker.Sequence, string.Empty, null, cancellationToken, definitionChange: true,
                        appliedSchema: new SqlCatalogSchemaState(schema.Hash, schema.CanonicalDocument, 0), dropped: plan.Dropped,
                        beforeTransaction: statement.CheckPublicationSpace, kept: keeping.Select(item => item.Shape).ToArray(),
                        treeOwners: reservedTrees,
                        publishTrees: _indexManager.PublishPrepared,
                        expectedKept: keeping.Select(item => item.Previous!).ToArray()).ConfigureAwait(false);
                }
                catch (Exception failure) when (SqlSchemaProvisioner.IsCommitUnconfirmed(failure))
                {
                    throw new SqlCatalogException(SqlProvisioningCodes.JointUnconfirmed, ErrorMessages.JointPublicationUnconfirmed, failure);
                }
                published = true;
                statement.Metrics.RecordSelfCommit();
                stage("CatalogPublished");
            }
            finally
            {
                try
                {
                    if (marker is not null)
                    {
                        await statement.Coordinator.RollbackAsync(marker, CancellationToken.None).ConfigureAwait(false);
                        if (published) { _catalog.ReleaseTruncatePublication(retiring, marker.Sequence); }
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
            // The engine reserve survives both abandonment and publication, including
            // reclamation WAL and dirty pages written after this statement ends.
            int before = failures.Count;
            try { if (!unconfirmed) { await RemoveDroppedIndexesAsync(statement, published ? oldIndexes : created, cancellationToken: CancellationToken.None).ConfigureAwait(false); } }
            catch (Exception cleanup) { failures.Add(cleanup); }
            if (!published && !unconfirmed)
            {
                _indexManager.DiscardPrepared();
                try { await _catalog.DiscardJointIntentAsync(CancellationToken.None, prepared, reservedTrees).ConfigureAwait(false); }
                catch (Exception cleanup) { failures.Add(cleanup); }
                finally { _catalog.AbandonStorageReplacement(prepared); _catalog.AbandonJointTreeOwners(reservedTrees); }
            }
            if (failures.Count != before) { throw new AggregateException(failures); }
        }
    }

    private static bool NeedsJointRowValidation(SqlCatalogTable previous, SqlCatalogTable target)
        => target.Columns.Where((column, ordinal) => !column.IsNullable && previous.Columns[ordinal].IsNullable).Any() ||
            target.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Check &&
                !previous.Constraints.Any(old => SameJointConstraint(constraint, old)));

    private bool NeedsJointReferenceValidation(SqlCatalogTable child, SqlCatalogConstraint constraint,
        SqlCatalogTable parent, IReadOnlyList<SqlCatalogIndex> descriptions)
    {
        if (!_catalog.TryGetTable(child.Schema, child.Name, out var oldChild) || child.ObjectId != oldChild.ObjectId ||
            !_catalog.TryGetTable(parent.Schema, parent.Name, out var oldParent) || parent.ObjectId != oldParent.ObjectId ||
            !oldChild.Constraints.Any(old => SameJointConstraint(constraint, old))) { return true; }
        var previous = _catalog.GetIndexes(parent.ObjectId).Where(index => index.IsUnique && index.Predicate is null &&
            SameConstraintColumns(index.ColumnNames, constraint.ReferencedColumns)).ToArray();
        var current = (descriptions.Any(index => index.TableObjectId == parent.ObjectId)
            ? descriptions.Where(index => index.TableObjectId == parent.ObjectId) : _catalog.GetIndexes(parent.ObjectId))
            .Where(index => index.IsUnique && index.Predicate is null && SameConstraintColumns(index.ColumnNames, constraint.ReferencedColumns)).ToArray();
        return previous.Length != current.Length || current.Any(index => !SameJointIndex(index, previous));
    }

    private static bool SameJointConstraint(SqlCatalogConstraint left, SqlCatalogConstraint right)
        => left.Kind == right.Kind && left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
            left.Columns.SequenceEqual(right.Columns, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(left.ReferencedSchema, right.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.ReferencedTable, right.ReferencedTable, StringComparison.OrdinalIgnoreCase) &&
            left.ReferencedColumns.SequenceEqual(right.ReferencedColumns, StringComparer.OrdinalIgnoreCase) &&
            left.CheckExpression == right.CheckExpression && left.CheckFunctionCalls.SequenceEqual(right.CheckFunctionCalls) &&
            left.OnDelete == right.OnDelete && left.OnUpdate == right.OnUpdate;

    private static SqlCatalogTable WithOwner(SqlCatalogTable shape, ulong id)
        => new(id, shape.Schema, shape.Name, shape.Columns, shape.PrimaryKeyColumns, shape.Owner, shape.OwningSchema, shape.Constraints);

    internal static bool SameJointIndex(SqlCatalogIndex index, IReadOnlyList<SqlCatalogIndex> previous)
        => previous.Any(old => SameJointIndex(index, old));

    private static bool SameJointIndex(SqlCatalogIndex index, SqlCatalogIndex old)
        => old.Name.Equals(index.Name, StringComparison.OrdinalIgnoreCase) && old.IsUnique == index.IsUnique &&
            old.IsPrimaryKey == index.IsPrimaryKey && old.Predicate == index.Predicate && old.KeyOrders.SequenceEqual(index.KeyOrders) &&
            old.ColumnNames.SequenceEqual(index.ColumnNames, StringComparer.OrdinalIgnoreCase);
}
