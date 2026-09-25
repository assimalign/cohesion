using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private sealed record SqlConstraintDelete(SqlCatalogTable Table, (PageId PageId, int SlotIndex) Location, object?[] Values);

    /// <summary>
    /// One parent row version a statement's outgoing foreign keys depend on: the
    /// referenced table and the packed location of the matching version, which is
    /// the entry-lock identity the row-write path already uses.
    /// </summary>
    private readonly record struct SqlParentReference(SqlCatalogTable Parent, PageId PageId, int SlotIndex)
    {
        public ulong EntryId => SqlRecordLocation.Pack(PageId, SlotIndex);
    }

    // ── Referential locking: parent-row granularity ────────────────────
    //
    // Referential integrity needs exactly one guarantee from the lock manager:
    // *before a statement reads a table's latest state for some key, every other
    // writer of that key must already be decided* (committed, or rolled back with
    // its undo complete). That is what makes `ConstraintCurrentSnapshot` reads
    // sound. Two conflicting locks on the **parent row** deliver it:
    //
    //   - A child writer takes `Shared` on the parent row version it matched
    //     (`AcquireParentRowLocksAsync`), then re-checks the version's stamps
    //     under that lock — the same latest-state discipline the row-write path
    //     applies to its own targets.
    //   - A child writer that *releases* a reference — deleting the row, or
    //     changing its key away — takes the same `Shared` lock on the parent row
    //     it is releasing (`CollectReleasedReferences`). Without it the scheme has
    //     an orphan window: a latest-state read treats an undecided tombstone as
    //     absence, so a parent delete would conclude the row is unreferenced,
    //     commit, and be contradicted when the child's transaction rolls back and
    //     restores the reference.
    //   - A parent writer takes `Exclusive` on every row it deletes or re-keys
    //     *before* reading child tables for incoming references. Shared and
    //     Exclusive are incompatible, so every child writer that acquired or
    //     released a reference to that row has been decided by the time the read
    //     happens.
    //
    // Table-grain locks stay intent-only: `IntentShared` on the adjacent tables a
    // statement reads for constraint purposes, which is compatible with other
    // writers' `IntentExclusive` and blocks only table-grain DDL. Two transactions
    // writing different tables of one reference graph no longer serialize.
    //
    // This replaces the original component-wide scheme, which took `Exclusive` on
    // every table in the transitive closure of the reference graph. In a normalized
    // schema that closure is usually the whole database, so a single foreign key
    // serialized nearly all writers. The cost of narrowing is that referential
    // waits can now form wait-for cycles (the closure's object-id ordering made
    // them impossible); they surface as the lock manager's requester-closes-cycle
    // abort, the same retryable deadlock the row-write path already produces.

    /// <summary>
    /// Takes the table-grain <see cref="LockMode.IntentShared"/> locks on the
    /// parent tables this table's foreign keys point at, in object-id order.
    /// Intent-shared is compatible with concurrent writers and blocks only
    /// table-grain DDL, so the parent definitions the statement binds against
    /// cannot change underneath it.
    /// </summary>
    private async ValueTask AcquireOutgoingReferenceIntentLocksAsync(SqlCatalogTable table, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var parents = new SortedSet<ulong>();
        foreach (var constraint in table.Constraints)
        {
            if (constraint.Kind != SqlCatalogConstraintKind.Reference)
            {
                continue;
            }

            if (!_catalog.TryGetTable(constraint.ReferencedSchema!, constraint.ReferencedTable!, out var parent))
            {
                throw new DatabaseException($"Referenced table '{constraint.ReferencedSchema}.{constraint.ReferencedTable}' is unavailable.");
            }

            parents.Add(parent.ObjectId);
        }

        await AcquireIntentSharedLocksAsync(parents, statement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the table-grain <see cref="LockMode.IntentShared"/> locks on the
    /// child tables that reference this table, in object-id order — the tables a
    /// parent delete or key change reads for incoming references.
    /// </summary>
    private async ValueTask AcquireIncomingReferenceIntentLocksAsync(SqlCatalogTable table, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var children = new SortedSet<ulong>();
        foreach (var (child, _) in IncomingReferences(table))
        {
            children.Add(child.ObjectId);
        }

        await AcquireIntentSharedLocksAsync(children, statement, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AcquireIntentSharedLocksAsync(SortedSet<ulong> objectIds, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        foreach (ulong objectId in objectIds)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence,
                LockResource.Object(objectId), LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes a transaction-duration <see cref="LockMode.Shared"/> lock on every
    /// parent row version the statement's foreign keys matched — sorted by
    /// (object, entry) and deduplicated — and re-validates each against its
    /// current stamps under that lock. Holding the shared lock is what keeps a
    /// concurrent parent delete from admitting an orphan; the latest-state check
    /// is what rejects a parent the statement's own snapshot still sees but a
    /// committed transaction has already removed.
    /// </summary>
    /// <param name="references">The matched parent versions; sorted in place.</param>
    /// <param name="statement">The executing statement.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <param name="validate">
    /// False for references a statement is *releasing* rather than taking on: the
    /// lock is still required, but a parent already tombstoned by a committed
    /// transaction is no reason to fail a statement that is dropping the reference
    /// to it.
    /// </param>
    /// <exception cref="TransactionAbortedException">A matched parent version was removed by a concurrently committed transaction.</exception>
    private async ValueTask AcquireParentRowLocksAsync(List<SqlParentReference> references, SqlStatementContext statement,
        CancellationToken cancellationToken, bool validate = true)
    {
        if (references.Count == 0)
        {
            return;
        }

        references.Sort(static (left, right) => left.Parent.ObjectId == right.Parent.ObjectId
            ? left.EntryId.CompareTo(right.EntryId)
            : left.Parent.ObjectId.CompareTo(right.Parent.ObjectId));

        (ulong ObjectId, ulong EntryId)? previous = null;
        foreach (var reference in references)
        {
            if (previous == (reference.Parent.ObjectId, reference.EntryId))
            {
                continue;
            }

            previous = (reference.Parent.ObjectId, reference.EntryId);
            await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence,
                LockResource.Entry(reference.Parent.ObjectId, reference.EntryId), LockMode.Shared, cancellationToken).ConfigureAwait(false);

            if (validate)
            {
                // A visible parent deleted after this snapshot cannot authorize a
                // new child; under the shared lock the check is also final, because
                // no concurrent writer can tombstone the version while it is held.
                EnsureLatestVersion(reference.Parent, reference.PageId, reference.SlotIndex, statement.Transaction.Sequence);
            }
        }
    }

    /// <summary>
    /// Resolves the parent rows a set of rows currently references — the rows a
    /// delete or key change is releasing — against latest state rather than the
    /// statement snapshot, because the version a future parent writer will lock is
    /// the live one, not the one this statement happens to see. The caller locks
    /// them through <see cref="AcquireParentRowLocksAsync"/> with validation off.
    /// </summary>
    /// <param name="table">The table the rows belong to.</param>
    /// <param name="rows">The row values whose references are being released.</param>
    /// <param name="references">The collection to append resolved parent versions to.</param>
    /// <param name="statement">The executing statement.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <param name="changed">
    /// When supplied, a constraint is resolved for a row only when this predicate
    /// accepts the (constraint, row index) pair — an UPDATE releases a reference
    /// only on the constraints whose columns it actually changes.
    /// </param>
    /// <param name="skip">
    /// A constraint to leave out: the cascade edge a row was reached by already
    /// points at a parent row this statement holds exclusively, so re-resolving it
    /// would cost a lookup to rediscover a lock already held.
    /// </param>
    private void CollectReleasedReferences(SqlCatalogTable table, IReadOnlyList<object?[]> rows, List<SqlParentReference> references,
        SqlStatementContext statement, CancellationToken cancellationToken,
        Func<SqlCatalogConstraint, int, bool>? changed = null, SqlCatalogConstraint? skip = null)
    {
        foreach (var constraint in table.Constraints)
        {
            if (constraint.Kind != SqlCatalogConstraintKind.Reference || ReferenceEquals(constraint, skip))
            {
                continue;
            }

            if (!_catalog.TryGetTable(constraint.ReferencedSchema!, constraint.ReferencedTable!, out var parent))
            {
                if (!string.Equals(constraint.ReferencedSchema, table.Schema, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(constraint.ReferencedTable, table.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parent = table;
            }

            for (int index = 0; index < rows.Count; index++)
            {
                object?[] row = rows[index];
                object?[] keys = constraint.Columns.Select(column => row[FindColumnOrdinal(table, column)]).ToArray();
                if (keys.Any(value => value is null) || (changed is not null && !changed(constraint, index)))
                {
                    continue; // MATCH SIMPLE, or a key this statement is not changing
                }

                foreach (var match in FindConstraintRows(parent, constraint.ReferencedColumns!, keys, statement, cancellationToken,
                    ConstraintCurrentSnapshot(statement)))
                {
                    references.Add(new SqlParentReference(parent, match.Location.PageId, match.Location.SlotIndex));
                }
            }
        }
    }

    private void EnsureCurrentDefinition(SqlCatalogTable expected)
    {
        var current = ReadCurrentTable(expected);
        EnsureSameIdentity(expected, current);
        if (!ReferenceEquals(expected, current))
        {
            throw new DatabaseException($"Table '{expected.Schema}.{expected.Name}' changed while the statement was waiting. Retry the statement.");
        }
    }

    // Reads the record space with no visibility filter at all — every stored
    // version, whoever wrote it. Latest-state constraint validation is what
    // prevents snapshot write skew, but it is only sound while the caller holds a
    // lock that every other writer of the keys it reads must conflict with:
    //
    //   - DML incoming-reference reads (parent delete, parent key change) hold the
    //     Exclusive row lock on the parent row before reading child tables; every
    //     child writer that matched that row held a Shared lock on it, so the read
    //     sees only decided writers.
    //   - DDL constraint backfills (ADD CONSTRAINT / ADD COLUMN) hold the
    //     Exclusive table lock on their own table and on every referenced parent
    //     table, which is the same guarantee at table grain.
    //
    // Ordinary parent visibility still uses the original statement snapshot below.
    private static TransactionSnapshot ConstraintCurrentSnapshot(SqlStatementContext statement)
        => new(statement.Transaction.Sequence, new TransactionSequence(ulong.MaxValue), new TransactionSequence(ulong.MaxValue), Array.Empty<TransactionSequence>());

    private IEnumerable<(SqlCatalogTable Child, SqlCatalogConstraint Constraint)> IncomingReferences(SqlCatalogTable parent)
        => _catalog.Tables.SelectMany(child => child.Constraints
            .Where(c => c.Kind == SqlCatalogConstraintKind.Reference &&
                string.Equals(c.ReferencedSchema, parent.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.ReferencedTable, parent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(c => (child, c)));

    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> FindConstraintRows(
        SqlCatalogTable table, IReadOnlyList<string> columns, object?[] keys, SqlStatementContext statement,
        CancellationToken cancellationToken, TransactionSnapshot? snapshot = null)
    {
        var candidate = _catalog.GetIndexes(table.ObjectId)
            .Select(index => (Metadata: index, Prefix: index.ColumnNames.TakeWhile(column => columns.Contains(column, StringComparer.OrdinalIgnoreCase)).ToArray()))
            .Where(index => index.Prefix.Length > 0)
            .OrderByDescending(index => index.Prefix.Length)
            .FirstOrDefault();
        var metadata = candidate.Metadata;
        IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> candidates;
        if (metadata is not null)
        {
            if (!_indexManager.TryGetIndex(table.ObjectId, metadata.Name, out var index))
            {
                throw new DatabaseException($"Constraint index '{metadata.Name}' is unavailable.");
            }

            statement.Metrics.AccessPath = $"constraint-seek:{metadata.Name}";
            var equalityValues = candidate.Prefix.Select(column => keys[Enumerable.Range(0, columns.Count)
                .First(ordinal => string.Equals(columns[ordinal], column, StringComparison.OrdinalIgnoreCase))]).ToArray();
            candidates = SeekRows(table, new SqlIndexSeekPath(metadata, equalityValues, null, null), index, statement, cancellationToken, snapshot);
        }
        else
        {
            statement.Metrics.AccessPath = "constraint-scan";
            candidates = Scan(table, statement, cancellationToken, snapshot);
        }
        int[] ordinals = columns.Select(column => FindColumnOrdinal(table, column)).ToArray();
        return candidates.Where(row => ordinals.Select((ordinal, i) => ValuesEqual(row.Values[ordinal], keys[i],
            table.Columns[ordinal].Collation ?? _catalog.DefaultCollation)).All(equal => equal));
    }

    private static bool SameConstraintColumns(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && left.All(column => right.Contains(column, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> CheckColumns(SqlExpression expression)
    {
        if (expression is SqlColumnReferenceExpression column)
        {
            yield return column.ColumnName;
        }
        foreach (var child in SqlPlanner.Children(expression))
        {
            foreach (string name in CheckColumns(child))
            {
                yield return name;
            }
        }
    }

    private static bool ValuesEqual(object? left, object? right, Collation? collation = null)
        => left is null || right is null ? left is null && right is null : SqlExpressionEvaluator.Compare(left, right, collation) == 0;

    private static object? SafeValue(object?[] values)
        => values.Length == 1 && values[0] is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double or bool
            ? values[0] : null;

    private static SqlConstraintViolationException Violation(SqlCatalogTable table, SqlCatalogConstraint constraint, object?[] values)
        => new(constraint.Name, $"{table.Schema}.{table.Name}", constraint.Kind == SqlCatalogConstraintKind.Check ? "CHECK" : "FOREIGN KEY", SafeValue(values));

    /// <summary>
    /// Validates checks and outgoing references for a set of candidate rows.
    /// </summary>
    /// <param name="table">The table the rows belong to.</param>
    /// <param name="rows">The candidate row values.</param>
    /// <param name="statement">The executing statement.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <param name="current">True to resolve parents against latest state (DDL backfills under the table's exclusive locks) instead of the statement snapshot.</param>
    /// <param name="references">
    /// When supplied, the matched parent versions are collected here instead of being
    /// validated inline; the caller then locks them through
    /// <see cref="AcquireParentRowLocksAsync"/>, which performs the latest-state check
    /// under the shared lock. DDL backfills pass null — they already hold the parent
    /// tables exclusively, so there is no row to lock against.
    /// </param>
    private void ValidateRows(SqlCatalogTable table, IReadOnlyList<object?[]> rows, SqlStatementContext statement,
        CancellationToken cancellationToken, bool current = false, List<SqlParentReference>? references = null)
    {
        foreach (var constraint in table.Constraints)
        {
            if (constraint.Kind == SqlCatalogConstraintKind.Check)
            {
                var expression = ParseCheck(constraint.CheckExpression!);
                var evaluator = new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, subqueryValues: _subqueryValues);
                foreach (var row in rows)
                {
                    // SQL UNKNOWN satisfies CHECK; only FALSE rejects a row.
                    object? result = evaluator.Evaluate(expression, row);
                    if (result is not null and not bool)
                    {
                        throw new DatabaseException($"CHECK '{constraint.Name}' must evaluate to BOOLEAN.");
                    }
                    if (result is false)
                    {
                        throw Violation(table, constraint, CheckColumns(expression).Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(column => row[FindColumnOrdinal(table, column)]).ToArray());
                    }
                }
                continue;
            }
            if (!_catalog.TryGetTable(constraint.ReferencedSchema!, constraint.ReferencedTable!, out var parent))
            {
                if (string.Equals(constraint.ReferencedSchema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(constraint.ReferencedTable, table.Name, StringComparison.OrdinalIgnoreCase))
                {
                    parent = table;
                }
                else
                {
                    throw new DatabaseException($"Referenced table '{constraint.ReferencedSchema}.{constraint.ReferencedTable}' does not exist.");
                }
            }
            foreach (var row in rows)
            {
                object?[] keys = constraint.Columns.Select(column => row[FindColumnOrdinal(table, column)]).ToArray();
                if (keys.Any(value => value is null))
                {
                    continue; // MATCH SIMPLE
                }

                bool selfMatch = parent.ObjectId == table.ObjectId && rows.Any(candidate =>
                    constraint.ReferencedColumns!.Select((column, i) => ValuesEqual(candidate[FindColumnOrdinal(table, column)], keys[i],
                        table.Columns[FindColumnOrdinal(table, column)].Collation ?? _catalog.DefaultCollation)).All(equal => equal));
                if (selfMatch)
                {
                    continue;
                }

                var matches = FindConstraintRows(parent, constraint.ReferencedColumns!, keys, statement, cancellationToken,
                    current ? ConstraintCurrentSnapshot(statement) : null).ToList();
                if (matches.Count == 0)
                {
                    throw Violation(table, constraint, keys);
                }

                foreach (var match in matches)
                {
                    if (references is null)
                    {
                        // A visible parent deleted after this snapshot cannot authorize a new child.
                        EnsureLatestVersion(parent, match.Location.PageId, match.Location.SlotIndex, statement.Transaction.Sequence);
                        continue;
                    }

                    references.Add(new SqlParentReference(parent, match.Location.PageId, match.Location.SlotIndex));
                }
            }
        }
    }

    private void ValidateParentUpdate(SqlCatalogTable table, object?[] oldValues, object?[] newValues,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        foreach (var (child, constraint) in IncomingReferences(table))
        {
            var keys = constraint.ReferencedColumns!.Select(column => oldValues[FindColumnOrdinal(table, column)]).ToArray();
            if (keys.Any(value => value is null))
            {
                continue;
            }
            bool changed = constraint.ReferencedColumns!.Select((column, i) => !ValuesEqual(newValues[FindColumnOrdinal(table, column)], keys[i],
                table.Columns[FindColumnOrdinal(table, column)].Collation ?? _catalog.DefaultCollation)).Any(value => value);
            if (changed && FindConstraintRows(child, constraint.Columns, keys, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Any())
            {
                throw Violation(child, constraint, keys);
            }
        }
    }

    /// <summary>
    /// Walks the cascade closure of one deleted row, locking as it descends: a row
    /// is exclusively locked before its child tables are read for incoming
    /// references, so the latest-state read only ever sees decided writers (see the
    /// referential-locking note above). The traversal deduplicates by packed
    /// location, so a cyclic cascade graph deletes each row exactly once.
    /// </summary>
    private async Task CollectCascadeDeletesAsync(SqlCatalogTable table, (PageId PageId, int SlotIndex) location, object?[] values,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions, HashSet<ulong> scannedTables,
        List<SqlParentReference> released, SqlCatalogConstraint? arrivedBy,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!deletions.TryAdd((table.ObjectId, SqlRecordLocation.Pack(location.PageId, location.SlotIndex)), new(table, location, values)))
        {
            return;
        }

        await AcquireRowWriteLocksAsync(statement, table.ObjectId, [location], cancellationToken).ConfigureAwait(false);
        if (scannedTables.Add(table.ObjectId))
        {
            await AcquireIncomingReferenceIntentLocksAsync(table, statement, cancellationToken).ConfigureAwait(false);
            await AcquireOutgoingReferenceIntentLocksAsync(table, statement, cancellationToken).ConfigureAwait(false);
        }

        // Deleting the row releases every reference it holds. The edge it was
        // reached by is excluded: that parent row is in the deletion set and is
        // already exclusively locked by this statement.
        CollectReleasedReferences(table, [values], released, statement, cancellationToken, skip: arrivedBy);

        foreach (var (child, constraint) in IncomingReferences(table).ToList())
        {
            var keys = constraint.ReferencedColumns!.Select(column => values[FindColumnOrdinal(table, column)]).ToArray();
            if (constraint.OnDelete == SqlCatalogReferentialAction.Restrict || keys.Any(value => value is null))
            {
                continue;
            }
            // Materialized before descending: the recursion awaits lock
            // acquisitions, and the storage iterator must not stay open across them.
            foreach (var match in FindConstraintRows(child, constraint.Columns, keys, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).ToList())
            {
                if (deletions.ContainsKey((child.ObjectId, SqlRecordLocation.Pack(match.Location.PageId, match.Location.SlotIndex))))
                {
                    continue;
                }

                await CollectCascadeDeletesAsync(child, match.Location, match.Values, deletions, scannedTables, released,
                    constraint, statement, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ValidateRestrictedDeletes(Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // Validate against the complete deletion set so physical row order and
        // constraint declaration order cannot change the outcome of one statement.
        // Every row in the set is already exclusively locked by the cascade walk,
        // which is what makes the latest-state child reads below sound.
        foreach (var deletion in deletions.Values)
        {
            foreach (var (child, constraint) in IncomingReferences(deletion.Table))
            {
                if (constraint.OnDelete != SqlCatalogReferentialAction.Restrict)
                {
                    continue;
                }
                var keys = constraint.ReferencedColumns!.Select(column => deletion.Values[FindColumnOrdinal(deletion.Table, column)]).ToArray();
                if (keys.Any(value => value is null))
                {
                    continue;
                }
                foreach (var match in FindConstraintRows(child, constraint.Columns, keys, statement, cancellationToken, ConstraintCurrentSnapshot(statement)))
                {
                    if (!deletions.ContainsKey((child.ObjectId, SqlRecordLocation.Pack(match.Location.PageId, match.Location.SlotIndex))))
                    {
                        throw Violation(child, constraint, keys);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Completes the deletion set's phase-one locks: the row locks are same-owner
    /// re-grants (the cascade walk took them as it descended, so they complete
    /// synchronously) and the unique-index key locks follow them, preserving the
    /// rows-before-keys class ordering of the lock-ordering rule.
    /// </summary>
    private async Task LockConstraintDeletesAsync(IEnumerable<SqlConstraintDelete> deletions, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        foreach (var group in deletions.GroupBy(deletion => deletion.Table.ObjectId).OrderBy(group => group.Key))
        {
            await AcquireRowWriteLocksAsync(statement, group.Key, group.Select(row => row.Location).ToList(), cancellationToken).ConfigureAwait(false);
            var hashes = new List<ulong>();
            foreach (var row in group)
            {
                CollectUniqueKeyHashes(GetLiveIndexes(row.Table), row.Table, row.Values, hashes);
            }

            await AcquireUniqueKeyLocksAsync(statement, group.Key, hashes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SqlExpression ParseCheck(string text)
    {
        var parsed = new SqlQueryParser().Parse($"SELECT * FROM __constraint WHERE {text}");
        if (parsed.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            parsed is not SqlQueryStatement { SqlExpression: SqlSelectExpression { Where: { } expression } })
        {
            throw new DatabaseException("CHECK requires a valid scalar predicate.");
        }

        return expression;
    }

    private static string ConstraintName(SqlCatalogTable table, SqlConstraintDefinition definition, int ordinal)
        => definition.Name ?? $"{definition.Kind}_{table.Name}_{string.Join('_', definition.Columns)}_{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private IReadOnlyList<SqlCatalogConstraint> BindConstraints(SqlCatalogTable table, IReadOnlyList<SqlConstraintDefinition> definitions)
    {
        var result = new List<SqlCatalogConstraint>();
        var names = new HashSet<string>(table.Constraints.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var index in _catalog.GetIndexes(table.ObjectId))
        {
            names.Add(index.Name);
        }

        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            string name = ConstraintName(table, definition, i);
            if (!names.Add(name))
            {
                throw new DatabaseException($"Constraint or index '{name}' already exists on '{table.Name}'.");
            }

            foreach (string column in definition.Columns)
            {
                FindColumnOrdinal(table, column);
            }

            if (definition.Kind is SqlConstraintKind.Unique or SqlConstraintKind.PrimaryKey)
            {
                continue;
            }

            if (definition.Kind == SqlConstraintKind.Check)
            {
                var expression = definition.CheckExpression ?? ParseCheck(definition.CheckExpressionText!);
                SqlPlanner.ValidateExpression(expression, new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, subqueryValues: _subqueryValues));
                ValidateCheckSyntax(expression, table, requireBoolean: true);
                result.Add(new SqlCatalogConstraint(name, SqlCatalogConstraintKind.Check, definition.Columns,
                    checkExpression: definition.CheckExpressionText));
                continue;
            }
            var referenced = definition.ReferencedTable!;
            string schema = referenced.SchemaName ?? table.Schema;
            bool self = string.Equals(schema, table.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(referenced.TableName, table.Name, StringComparison.OrdinalIgnoreCase);
            SqlCatalogTable parent;
            if (self)
            {
                parent = table;
            }
            else if (!_catalog.TryGetTable(schema, referenced.TableName, out parent!))
            {
                throw new DatabaseException($"Referenced table '{schema}.{referenced.TableName}' does not exist.");
            }

            if (definition.Columns.Count != definition.ReferencedColumns.Count || definition.Columns.Count == 0)
            {
                throw new DatabaseException($"FOREIGN KEY '{name}' requires matching nonempty column lists.");
            }

            for (int column = 0; column < definition.Columns.Count; column++)
            {
                int childOrdinal = FindColumnOrdinal(table, definition.Columns[column]);
                int parentOrdinal = FindColumnOrdinal(parent, definition.ReferencedColumns[column]);
                if (table.Columns[childOrdinal].Type.Type != parent.Columns[parentOrdinal].Type.Type)
                {
                    throw new DatabaseException($"FOREIGN KEY '{name}' requires matching column types.");
                }
                if (table.Columns[childOrdinal].Type.Type == DatabaseType.String
                    && (table.Columns[childOrdinal].Collation ?? _catalog.DefaultCollation)
                        != (parent.Columns[parentOrdinal].Collation ?? _catalog.DefaultCollation))
                {
                    throw new DatabaseException($"FOREIGN KEY '{name}' requires matching column collations.");
                }
            }
            bool unique = (self && SameConstraintColumns(parent.PrimaryKeyColumns, definition.ReferencedColumns)) ||
                _catalog.GetIndexes(parent.ObjectId).Any(index => index.IsUnique && SameConstraintColumns(index.ColumnNames, definition.ReferencedColumns)) ||
                (self && definitions.Any(d => d.Kind is SqlConstraintKind.Unique or SqlConstraintKind.PrimaryKey && SameConstraintColumns(d.Columns, definition.ReferencedColumns)));
            if (!unique)
            {
                throw new DatabaseException($"FOREIGN KEY '{name}' must reference a primary key or unique index.");
            }

            result.Add(new SqlCatalogConstraint(name, SqlCatalogConstraintKind.Reference, definition.Columns, schema, referenced.TableName,
                definition.ReferencedColumns, onDelete: definition.OnDelete == SqlReferentialAction.Cascade ? SqlCatalogReferentialAction.Cascade : SqlCatalogReferentialAction.Restrict));
        }
        return result;
    }
    private static void ValidateCheckSyntax(SqlExpression expression, SqlCatalogTable table, bool requireBoolean)
    {
        if (expression is SqlParameterExpression or SqlSubqueryExpression or SqlExistsExpression or SqlCastExpression or SqlStarExpression
            or SqlInExpression { Subquery: not null })
        {
            throw new DatabaseException("CHECK requires deterministic row expressions without parameters, subqueries, or casts.");
        }
        if (expression is SqlFunctionCallExpression function && function.FunctionName.ToUpperInvariant() is not ("COALESCE" or "UPPER" or "LOWER" or "LENGTH" or "ABS"))
        {
            throw new DatabaseException($"Function '{function.FunctionName}' is not supported in CHECK.");
        }
        foreach (var child in SqlPlanner.Children(expression))
        {
            ValidateCheckSyntax(child, table, requireBoolean: false);
        }
        if (expression is SqlBinaryExpression { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or } logical)
        {
            ValidateCheckSyntax(logical.Left, table, requireBoolean: true);
            ValidateCheckSyntax(logical.Right, table, requireBoolean: true);
        }
        if (expression is SqlUnaryExpression { Operator: SqlUnaryOperator.Not } negation)
        {
            ValidateCheckSyntax(negation.Operand, table, requireBoolean: true);
        }
        if (!requireBoolean)
        {
            return;
        }
        bool boolean = expression switch
        {
            SqlBinaryExpression binary => binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or SqlBinaryOperator.LessThan or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterOrEqual or SqlBinaryOperator.And or SqlBinaryOperator.Or,
            SqlUnaryExpression unary => unary.Operator == SqlUnaryOperator.Not,
            SqlIsNullExpression or SqlBetweenExpression or SqlInExpression or SqlLikeExpression => true,
            SqlLiteralExpression literal => literal.LiteralType is SqlLiteralType.Boolean or SqlLiteralType.Null,
            SqlColumnReferenceExpression column => table.Columns[FindColumnOrdinal(table, column.ColumnName)].Type.Type == DatabaseType.Boolean,
            SqlFunctionCallExpression functionCall => string.Equals(functionCall.FunctionName, "COALESCE", StringComparison.OrdinalIgnoreCase),
            SqlCaseExpression => true,
            _ => false,
        };
        if (!boolean)
        {
            throw new DatabaseException("CHECK requires a BOOLEAN predicate.");
        }
        if (expression is SqlFunctionCallExpression coalesce)
        {
            foreach (var argument in coalesce.Arguments)
            {
                ValidateCheckSyntax(argument, table, requireBoolean: true);
            }
        }
        if (expression is SqlCaseExpression caseExpression)
        {
            foreach (var branch in caseExpression.WhenClauses)
            {
                if (caseExpression.Input is null)
                {
                    ValidateCheckSyntax(branch.Condition, table, requireBoolean: true);
                }
                ValidateCheckSyntax(branch.Result, table, requireBoolean: true);
            }
            if (caseExpression.ElseResult is not null)
            {
                ValidateCheckSyntax(caseExpression.ElseResult, table, requireBoolean: true);
            }
        }
    }

}
