using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action<SqlCatalogTable>? ReferenceIntentsDiscovered { get; set; }

    private static readonly string _errorEnsureSnapshotSeesCascadeRowFailure3 = ErrorMessages.EnsureSnapshotSeesCascadeRowFailure3;

    private static readonly string _errorEnsureSnapshotSeesCascadeRowFailure = ErrorMessages.EnsureSnapshotSeesCascadeRowFailure;

    private sealed record SqlConstraintDelete(SqlCatalogTable Table, (PageId PageId, int SlotIndex) Location, object?[] Values);
    [global::System.Diagnostics.DebuggerDisplay("SqlConstraintUpdate")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.SqlConstraintUpdate.DebugView))]
    private sealed record SqlConstraintUpdate(SqlCatalogTable Table, (PageId PageId, int SlotIndex) Location,
        object?[] OldValues, object?[] NewValues, HashSet<int> Assigned)
    {
        // Referential actions fill this only after every assignment to the final row is known.
        internal byte[] NewVersion { get; set; } = [];

        private sealed class DebugView
        {
            private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.SqlConstraintUpdate _target;

            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.SqlConstraintUpdate value)
            {
                _target = value;
            }

            public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Table => _target.Table;
            public (global::Assimalign.Cohesion.Database.Storage.PageId PageId, int SlotIndex) Location => _target.Location;
            public object?[] OldValues => _target.OldValues;
            public object?[] NewValues => _target.NewValues;
            public global::System.Collections.Generic.HashSet<int> Assigned => _target.Assigned;
        }
    }

    /// <summary>
    /// One row on the cascade walk's path (<see cref="CollectCascadeDeletesAsync"/>),
    /// holding what a recursive call held in its frame: the row, the references into
    /// its table, and how far the walk has got through them.
    /// </summary>
    private sealed class SqlCascadeFrame
    {
        public SqlCascadeFrame(SqlCatalogTable table, object?[] values, IReadOnlyList<SqlBoundReference> incoming,
            int lastCascade)
        {
            Table = table;
            Values = values;
            Incoming = incoming;
            LastCascade = lastCascade;
        }

        /// <summary>The table the row belongs to.</summary>
        public SqlCatalogTable Table { get; }

        /// <summary>The row's values, which supply the keys its child rows are found by.</summary>
        public object?[] Values { get; }

        /// <summary>The references into <see cref="Table"/>, read once the row was locked.</summary>
        public IReadOnlyList<SqlBoundReference> Incoming { get; }

        /// <summary>
        /// The index in <see cref="Incoming"/> of the last reference this row's
        /// deletion can cascade through (see <see cref="CascadeKeys"/>), or -1 when
        /// there is none. The references after it are passed over without a lookup.
        /// </summary>
        public int LastCascade { get; }

        /// <summary>The index in <see cref="Incoming"/> of the reference being walked; -1 before the first.</summary>
        public int Reference { get; set; } = -1;

        /// <summary>The current reference's child rows, or null between references.</summary>
        public List<((PageId PageId, int SlotIndex) Location, object?[] Values)>? Matches { get; set; }

        /// <summary>The index in <see cref="Matches"/> of the next child row to consider.</summary>
        public int Match { get; set; }

        /// <summary>
        /// Whether the walk has handed out every row this frame can descend into:
        /// it has reached its last cascading reference and that reference's matches
        /// are exhausted. Any reference after it is a restricting one, or one whose
        /// key is null in this row, which the walk would pass over without a lookup.
        /// </summary>
        public bool IsComplete => Matches is null && Reference >= LastCascade;
    }

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
    // The guarantee holds while the locked row is the parent's live version. A
    // statement whose snapshot holds a version that a committed transaction has
    // since replaced locks that stale version, while new child writers lock the
    // replacement, so its read can return a child whose writer is still in flight.
    // Such a statement writes nothing below the stale version: a cascade under a
    // fixed snapshot fails on that child (`EnsureSnapshotSeesCascadeRow`), a
    // RESTRICT or key-change check reports it as a violation, and otherwise the
    // stale version fails its phase-two `EnsureLatestVersion`, which comes before
    // every row found below it because the cascade builds its deletion set
    // pre-order.
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
    // abort, the same retryable deadlock the row-write path already produces. ON CONFLICT
    // instead tries parent-row grants without waiting: on contention it releases its
    // unwritten arbitration's new grants, waits through the lock manager, then restarts
    // the whole arbitration (#1668). Plain INSERT takes parent rows before unique keys.

    // Prepare adjacent IS before own IX. Non-cascade preparation releases new grants
    // before waiting: FK DDL takes own X then referenced-parent X. Cascades report a
    // refusal to the statement's bounded release/retry policy; from its third refusal
    // it retains closure grants and queues with ordinary deadlock detection.
    private async ValueTask AcquireReferenceIntentLocksAsync(SqlCatalogTable table, SqlStatementContext statement,
        CancellationToken cancellationToken, bool incoming = true, bool outgoing = true, bool cascade = false)
    {
        var references = statement.References;
        incoming &= !references.IncomingLocks.Contains(table.ObjectId);
        outgoing &= !references.OutgoingLocks.Contains(table.ObjectId);
        if (!incoming && !outgoing) { return; }
        var locks = statement.Coordinator.LockManager;
        var owner = statement.Transaction.Sequence;
        var mark = locks.MarkStatement(owner);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = _catalog.BindingGeneration;
            var adjacent = new SortedSet<ulong>();
            if (outgoing)
            {
                foreach (var constraint in table.Constraints)
                {
                    if (constraint.Kind == SqlCatalogConstraintKind.Reference)
                    {
                        // Refresh after any wait; a parent version can change independently.
                        references.Parents.Remove(constraint);
                        if (ReferenceParent(table, constraint, statement) is { } parent) { adjacent.Add(parent.ObjectId); }
                    }
                }
            }
            if (incoming)
            {
                foreach (var reference in IncomingReferences(table, statement, cancellationToken))
                {
                    adjacent.Add(reference.Child.ObjectId);
                }
            }
            ReferenceIntentsDiscovered?.Invoke(table);
            adjacent.Remove(table.ObjectId);
            ulong blocked = table.ObjectId;
            LockMode mode = LockMode.IntentExclusive;
            bool acquired = true;
            foreach (ulong objectId in adjacent)
            {
                if (!locks.TryAcquire(owner, LockResource.Object(objectId), LockMode.IntentShared))
                {
                    blocked = objectId;
                    mode = LockMode.IntentShared;
                    acquired = false;
                    break;
                }
            }
            if (acquired) { acquired = locks.TryAcquire(owner, LockResource.Object(table.ObjectId), LockMode.IntentExclusive); }
            if (!acquired)
            {
                if (cascade) { throw new SqlCascadeLockRetry(LockResource.Object(blocked), mode); }
                locks.ReleaseSince(mark);
                // Waiting owns no new table grants; completion is only a rendezvous.
                // Reacquire the entire graph because publication can precede the wakeup.
                await locks.AcquireAsync(owner, LockResource.Object(blocked), mode, cancellationToken).ConfigureAwait(false);
                locks.ReleaseSince(mark);
                if (incoming) { references.Incoming.Remove(table.ObjectId); }
                continue;
            }
            EnsureCurrentDefinition(table);
            if (generation != _catalog.BindingGeneration)
            {
                // Physical publication can replace an outgoing parent between discovery
                // and its IS grant. Refresh parents before probing or locking their rows.
                // Under own IX no new incoming FK can publish (it needs parent X).
                // Rebuild once under that lock and try any newly discovered children.
                if (incoming) { references.Incoming.Remove(table.ObjectId); }
                continue;
            }
            if (incoming)
            {
                foreach (var reference in IncomingReferences(table, statement, cancellationToken))
                {
                    EnsureCurrentDefinition(reference.Child);
                }
                references.IncomingLocks.Add(table.ObjectId);
                statement.Metrics.ReferenceIntentLockBatches++;
            }
            if (outgoing)
            {
                references.OutgoingLocks.Add(table.ObjectId);
                statement.Metrics.ReferenceIntentLockBatches++;
            }
            return;
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
        CancellationToken cancellationToken, bool validate = true, bool cascade = false)
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
            await AcquirePreparationLockAsync(statement,
                LockResource.Entry(reference.Parent.ObjectId, reference.EntryId), LockMode.Shared, cascade, cancellationToken).ConfigureAwait(false);

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
        Func<SqlBoundReference, int, bool>? changed = null, SqlCatalogConstraint? skip = null)
    {
        foreach (var constraint in table.Constraints)
        {
            if (constraint.Kind != SqlCatalogConstraintKind.Reference || ReferenceEquals(constraint, skip))
            {
                continue;
            }

            var parent = ReferenceParent(table, constraint, statement);
            if (parent is null) { continue; }
            var reference = BindReference(table, constraint, parent, statement, cancellationToken);

            for (int index = 0; index < rows.Count; index++)
            {
                object?[] row = rows[index];
                object?[] keys = reference.ChildKey.Read(row);
                if (keys.Any(value => value is null) || (changed is not null && !changed(reference, index)))
                {
                    continue; // MATCH SIMPLE, or a key this statement is not changing
                }

                foreach (var match in FindConstraintRows(reference.ParentKey, keys, statement, cancellationToken,
                    ConstraintCurrentSnapshot(statement)))
                {
                    references.Add(new SqlParentReference(parent, match.Location.PageId, match.Location.SlotIndex));
                }
            }
        }
    }

    private void EnsureCurrentDefinition(SqlCatalogTable expected)
    {
        if (!_catalog.TryGetTable(expected.Schema, expected.Name, out var current) || !ReferenceEquals(expected, current))
        {
            string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StatementTableChanged, expected.Schema, expected.Name);
            throw new DatabaseTransactionAbortedException(message,
                [new Diagnostic { Code = "CS04006", Severity = DiagnosticSeverity.Error, Message = message }]);
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
    // A latest-state read can return a version newer than a snapshot fixed at the
    // transaction's begin; a cascade that would delete one fails first-updater-wins
    // instead (EnsureSnapshotSeesCascadeRow, #1370).
    private static TransactionSnapshot ConstraintCurrentSnapshot(SqlStatementContext statement)
        => new(statement.Transaction.Sequence, new TransactionSequence(ulong.MaxValue), new TransactionSequence(ulong.MaxValue), Array.Empty<TransactionSequence>());

    private IReadOnlyList<SqlBoundReference> IncomingReferences(
        SqlCatalogTable parent, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var incoming = statement.References.Incoming;
        if (incoming.TryGetValue(parent.ObjectId, out var cached)) { return cached; }
        var result = new List<SqlBoundReference>();
        foreach (var child in _catalog.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statement.Metrics.ReferenceCatalogTableVisits++;
            foreach (var constraint in child.Constraints)
            {
                if (constraint.Kind == SqlCatalogConstraintKind.Reference &&
                    string.Equals(constraint.ReferencedSchema, parent.Schema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(constraint.ReferencedTable, parent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(BindReference(child, constraint, parent, statement, cancellationToken));
                }
            }
        }
        statement.Metrics.IncomingReferenceBuilds++;
        incoming.Add(parent.ObjectId, result);
        return result;
    }

    private SqlBoundReference BindReference(SqlCatalogTable child, SqlCatalogConstraint constraint,
        SqlCatalogTable parent, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var bindings = statement.References.Bindings;
        if (bindings.TryGetValue(constraint, out var bound) && ReferenceEquals(bound.Child, child) && ReferenceEquals(bound.Parent, parent)) { return bound; }
        // Discovery must not bind an unlocked child that DDL can still drop/replace.
        bound = new SqlBoundReference(child, constraint, parent, _definitions);
        bindings[constraint] = bound;
        return bound;
    }

    private SqlCatalogTable? ReferenceParent(SqlCatalogTable child, SqlCatalogConstraint constraint, SqlStatementContext statement)
    {
        var parents = statement.References.Parents;
        if (parents.TryGetValue(constraint, out var parent)) { return parent; }
        if (!_catalog.TryGetTable(constraint.ReferencedSchema!, constraint.ReferencedTable!, out parent))
        {
            if (string.Equals(constraint.ReferencedSchema, child.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(constraint.ReferencedTable, child.Name, StringComparison.OrdinalIgnoreCase)) { parent = child; }
            // Preserve the original row-check continue for a missing non-self parent.
            // Before locking, FK removal followed by parent DROP can invalidate the
            // child's plan; preparation checks that target version under IX and codes
            // the retry. A stable SQL FK cannot lose its parent under the adjacent IS.
            else { return null; }
        }
        parents.Add(constraint, parent);
        return parent;
    }

    private IEnumerable<(SqlCatalogTable Child, SqlCatalogConstraint Constraint)> IncomingReferences(SqlCatalogTable parent)
        => _catalog.Tables.SelectMany(child => child.Constraints
            .Where(c => c.Kind == SqlCatalogConstraintKind.Reference &&
                string.Equals(c.ReferencedSchema, parent.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.ReferencedTable, parent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(c => (child, c)));

    private SqlConstraintLookup ConstraintLookup(SqlBoundConstraintKey key, SqlStatementContext statement)
    {
        var lookups = statement.References.Lookups;
        if (lookups.TryGetValue(key, out var lookup)) { return lookup; }
        SqlCatalogIndex? selected = null;
        int[] prefix = [], indexOrdinals = [];
        var bound = _definitions.Get(key.Table);
        foreach (var metadata in _catalog.GetIndexes(key.Table.ObjectId))
        {
            int[] ordinals = bound.GetKey(metadata.ColumnNames).Ordinals;
            var mapping = new List<int>();
            foreach (int ordinal in ordinals)
            {
                int position = Array.IndexOf(key.Ordinals, ordinal);
                if (position < 0) { break; }
                mapping.Add(position);
            }
            if (mapping.Count > prefix.Length)
            {
                selected = metadata;
                prefix = mapping.ToArray();
                indexOrdinals = ordinals;
            }
        }
        BTreeIndex? index = null;
        if (selected is not null && !_indexManager.TryGetIndex(key.Table.ObjectId, selected.Name, out index))
        {
            throw new SqlExecutionException(SqlExecutionException.InternalErrorCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ConstraintIndexUnavailable, selected.Name));
        }
        lookup = new SqlConstraintLookup(selected, index, prefix, indexOrdinals);
        lookups.Add(key, lookup);
        statement.Metrics.ConstraintLookupBuilds++;
        return lookup;
    }

    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> FindConstraintRows(
        SqlBoundConstraintKey key, object?[] keys, SqlStatementContext statement,
        CancellationToken cancellationToken, TransactionSnapshot? snapshot = null)
    {
        var table = key.Table;
        var lookup = ConstraintLookup(key, statement);
        statement.Metrics.AccessPath = lookup.AccessPath;
        IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> candidates;
        if (lookup.Metadata is { } metadata)
        {
            var equalityValues = new object?[lookup.PrefixKeyOrdinals.Length];
            for (int ordinal = 0; ordinal < equalityValues.Length; ordinal++) { equalityValues[ordinal] = keys[lookup.PrefixKeyOrdinals[ordinal]]; }
            candidates = SeekRows(table, new SqlIndexSeekPath(metadata, equalityValues, null, null), lookup.Index!, statement,
                cancellationToken, snapshot, lookup.IndexOrdinals);
        }
        else { candidates = Scan(table, statement, cancellationToken, snapshot); }
        foreach (var row in candidates)
        {
            bool match = true;
            for (int ordinal = 0; ordinal < key.Ordinals.Length; ordinal++)
            {
                if (!ValuesEqual(row.Values[key.Ordinals[ordinal]], keys[ordinal], key.Collations[ordinal], statement.Metrics))
                {
                    match = false;
                    break;
                }
            }
            if (match) { yield return row; }
        }
    }

    private static bool SameConstraintColumns(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && left.All(column => right.Contains(column, StringComparer.OrdinalIgnoreCase));

    private static bool ValuesEqual(object? left, object? right, Collation? collation = null, SqlStatementMetrics? metrics = null)
    {
        if (metrics is not null) { metrics.ConstraintKeyComparisons++; }
        return left is null || right is null ? left is null && right is null : SqlExpressionEvaluator.Compare(left, right, collation) == 0;
    }

    private static object? SafeValue(object?[] values)
        => values.Length == 1 && values[0] is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double or bool
            ? values[0] : null;

    private static SqlConstraintViolationException Violation(SqlCatalogTable table, SqlCatalogConstraint constraint, object?[] values)
        => new(constraint.Name, $"{table.Schema}.{table.Name}", constraint.Kind == SqlCatalogConstraintKind.Check ? "CHECK" : "FOREIGN KEY", SafeValue(values));

    /// <summary>
    /// Checks the values a write assigns to columns of a domain (phase E3): each domain's check is
    /// evaluated on its column's value as a column CHECK is, so only FALSE refuses the row, and a
    /// column whose domain the engine does not register refuses any write that assigns it with
    /// <c>CS02010</c>. PostgreSQL likewise checks a domain where a value is coerced to it, on
    /// INSERT for every column, its default included, and on UPDATE for the columns it sets
    /// (<c>ExecEvalConstraintCheck</c>, <c>src/backend/executor/execExprInterp.c</c>).
    /// </summary>
    /// <param name="bound">The bound table version the row is written under.</param>
    /// <param name="values">The row's values.</param>
    /// <param name="evaluator">The statement's evaluator.</param>
    /// <param name="ordinals">The columns the write assigns; null for every column.</param>
    /// <exception cref="SqlConstraintViolationException">A domain's check returned FALSE.</exception>
    /// <exception cref="SqlEvaluationException">A column's domain does not resolve (<c>CS02010</c>), or a check failed (<c>CS05007</c>).</exception>
    private static void CheckDomains(SqlBoundTable bound, object?[] values, SqlExpressionEvaluator evaluator, int[]? ordinals = null)
    {
        if (bound.Domains.Count == 0)
        {
            return;
        }

        if (ordinals is null)
        {
            foreach (var domain in bound.Domains)
            {
                CheckDomain(bound.Table, domain, values, evaluator);
            }

            return;
        }

        foreach (int ordinal in ordinals)
        {
            if (bound.DomainAt(ordinal) is { } domain)
            {
                CheckDomain(bound.Table, domain, values, evaluator);
            }
        }
    }

    private static void CheckDomain(SqlCatalogTable table, SqlBoundDomain domain, object?[] values, SqlExpressionEvaluator evaluator)
    {
        if (domain.Unresolved is { } unresolved)
        {
            throw SqlEvaluationException.UnregisteredDomain(unresolved);
        }

        // CoerceForColumn (including bound DEFAULTs) has already applied the base type's
        // assignment bounds. The domain check sees that rounded/truncated value (#1418).
        if (domain.Check is { } check && evaluator.Evaluate(check, values) is false)
        {
            throw new SqlConstraintViolationException(domain.Name, $"{table.Schema}.{table.Name}", "DOMAIN", SafeValue([values[domain.Ordinal]]));
        }
    }

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
        CancellationToken cancellationToken, bool current = false, List<SqlParentReference>? references = null,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>? deletions = null,
        SqlConstraintUpdates? updates = null, bool checksOnly = false, bool skipChecks = false,
        TransactionSnapshot? foreignKeySnapshot = null)
    {
        // The table version's CHECK predicates were parsed and bound once, when the catalog
        // loaded or the DDL that produced this version ran; a write only evaluates the bound
        // trees, which carry their column ordinals, functions and collations.
        var bound = _definitions.Get(table, cancellationToken);
        var evaluator = ExecutionEvaluator(null, cancellationToken, containsLike: bound.ChecksContainLike,
            transactionTimestamp: statement.TransactionStart);
        foreach (var constraint in table.Constraints)
        {
            if (constraint.Kind == SqlCatalogConstraintKind.Check)
            {
                if (skipChecks) { continue; }
                var check = bound.GetCheck(constraint);
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // SQL UNKNOWN satisfies CHECK; only FALSE rejects a row.
                    object? result;
                    try { result = evaluator.Evaluate(check.Bound, row); }
                    catch (DatabaseException failure) { throw SqlExecutionException.FromPersisted(failure); }
                    if (result is not null and not bool)
                    {
                        throw SqlEvaluationException.BooleanRequired("CHECK", SqlExpressionEvaluator.OperandTypeName(result));
                    }
                    if (result is false)
                    {
                        throw Violation(table, constraint, check.ColumnOrdinals.Select(ordinal => row[ordinal]).ToArray());
                    }
                }
                continue;
            }
            if (checksOnly) { continue; }
            var parent = ReferenceParent(table, constraint, statement);
            if (parent is null) { continue; }
            var reference = BindReference(table, constraint, parent, statement, cancellationToken);

            HashSet<object?[]>? selfKeys = null;
            if (parent.ObjectId == table.ObjectId)
            {
                int[] ordinals = reference.ParentKey.Ordinals;
                selfKeys = new(new GroupKeyComparer(reference.ParentKey.Collations, statement.Metrics));
                foreach (var candidate in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    statement.Metrics.SelfReferenceCandidateVisits++;
                    selfKeys.Add(ordinals.Select(ordinal => candidate[ordinal]).ToArray());
                }
            }
            bool snapshotChecked = false;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object?[] keys = reference.ChildKey.Read(row);
                if (keys.Any(value => value is null))
                {
                    continue; // MATCH SIMPLE
                }

                if (selfKeys is not null) { statement.Metrics.SelfReferenceProbes++; }
                if (selfKeys?.Contains(keys) == true)
                {
                    continue;
                }

                if (!current && !snapshotChecked)
                {
                    // Only a real parent probe needs historical versions. ON CONFLICT
                    // under RC probes with refreshed visibility after arbitration waits.
                    if (_catalog.SnapshotPredatesReplacement(parent, foreignKeySnapshot ?? statement.Snapshot))
                    {
                        string message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                            ErrorMessages.StatementTableChanged, parent.Schema, parent.Name);
                        throw new DatabaseTransactionAbortedException(message,
                            [new Diagnostic { Code = "CS04006", Severity = DiagnosticSeverity.Error, Message = message }]);
                    }
                    snapshotChecked = true;
                }

                var matches = FinalConstraintRows(reference.ParentKey, keys, statement, cancellationToken,
                    deletions, updates, current ? ConstraintCurrentSnapshot(statement) : foreignKeySnapshot).ToList();
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
        SqlStatementContext statement, CancellationToken cancellationToken,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>? deletions = null,
        SqlConstraintUpdates? updates = null)
    {
        foreach (var reference in IncomingReferences(table, statement, cancellationToken))
        {
            var (child, constraint) = reference;
            var keys = reference.ParentKey.Read(oldValues);
            if (keys.Any(value => value is null))
            {
                continue;
            }
            bool changed = reference.ParentKey.Ordinals.Select((ordinal, i) => !ValuesEqual(newValues[ordinal], keys[i],
                reference.ParentKey.Collations[i], statement.Metrics)).Any(value => value);
            // A different replacement may retain the old referenced key (a swap). Only
            // removing that key from the final parent state can orphan an incoming child.
            if (changed && updates?.Find(reference.ParentKey, keys,
                statement.Metrics, cancellationToken).Any() == true) { continue; }
            if (changed && FinalConstraintRows(reference.ChildKey, keys, statement, cancellationToken,
                deletions, updates, ConstraintCurrentSnapshot(statement)).Any())
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
    /// <remarks>
    /// The walk is depth-first and pre-order: a row joins the deletion set, is
    /// locked and releases its own references before any row below it is read.
    /// It keeps its path in an explicit stack of <see cref="SqlCascadeFrame"/>s on
    /// the heap rather than recursing once per level, because the depth of a
    /// cascade is the data's, not the schema's: a self-referencing chain is as
    /// deep as it is long, and recursing through it overflowed the stack of the
    /// thread running the statement, which ends the process (#1164). Each frame
    /// holds exactly what a recursive call held, so the order rows are visited,
    /// locked and added to the deletion set is the recursion's.
    /// <para>
    /// A frame leaves the path as soon as it hands out the last matching row of
    /// its last cascading reference, before that row's subtree is walked: the
    /// recursive call had nothing left to do but pass over its restricting
    /// references and return once the subtree did. A self-referencing chain
    /// therefore walks with one frame on the path however long it is, whatever
    /// restricting references into its table follow the self-reference; the path
    /// grows only with the rows that still have matches, or cascading references,
    /// left to visit.
    /// </para>
    /// </remarks>
    private async Task CollectCascadeDeletesAsync(SqlCatalogTable table, (PageId PageId, int SlotIndex) location, object?[] values,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions, HashSet<ulong> scannedTables,
        List<SqlParentReference> released, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var root = await EnterCascadeRowAsync(table, location, values, arrivedBy: null, deletions, scannedTables, released,
            statement, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return; // an earlier target's cascade already reached this row
        }

        var path = new Stack<SqlCascadeFrame>();
        path.Push(root);
        while (path.TryPeek(out var frame))
        {
            // Entering a row takes only its locks, which an uncontended grant does
            // not check for cancellation, and one lookup can hand out any number of
            // rows (a wide fan-out into a table with no cascading references does no
            // further lookup). Checking each step makes every step cancellable.
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNextCascadeRow(frame, deletions, statement, cancellationToken, out var edge, out var match))
            {
                path.Pop(); // every row below this one is collected
                continue;
            }

            if (frame.IsComplete)
            {
                path.Pop(); // that was its last child row
            }

            var next = await EnterCascadeRowAsync(edge.Child, match.Location, match.Values, edge.Constraint, deletions, scannedTables,
                released, statement, cancellationToken).ConfigureAwait(false);
            if (next is not null)
            {
                path.Push(next);
            }
        }
    }

    /// <summary>
    /// Visits one row of a cascade: adds it to the deletion set, takes its
    /// exclusive lock, checks a child row against a snapshot fixed at the
    /// transaction's begin (<see cref="EnsureSnapshotSeesCascadeRow"/>), takes
    /// the intent locks on the tables around it the first time the walk reaches
    /// its table, and collects the references deleting it releases. Returns the
    /// frame that descends from it, or null when the row is already in the
    /// deletion set.
    /// </summary>
    private async ValueTask<SqlCascadeFrame?> EnterCascadeRowAsync(SqlCatalogTable table, (PageId PageId, int SlotIndex) location,
        object?[] values, SqlCatalogConstraint? arrivedBy, Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions,
        HashSet<ulong> scannedTables, List<SqlParentReference> released, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!deletions.TryAdd((table.ObjectId, SqlRecordLocation.Pack(location.PageId, location.SlotIndex)), new(table, location, values)))
        {
            return null;
        }

        if (statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget) is { } memory)
        {
            memory.Retain(values);
            memory.Reserve(256); // deletion record, dictionary growth and cascade work
        }

        await AcquireRowWriteLocksAsync(statement, table.ObjectId, [location], cancellationToken, cascade: true).ConfigureAwait(false);

        // A child row was found in latest state, which can be newer than a snapshot fixed at the
        // transaction's begin; a target of the statement itself was found through that snapshot.
        if (arrivedBy is not null)
        {
            EnsureSnapshotSeesCascadeRow(table, location, statement);
        }

        if (scannedTables.Add(table.ObjectId))
        {
            EnsureCurrentDefinition(table);
            await AcquireReferenceIntentLocksAsync(table, statement, cancellationToken, cascade: true).ConfigureAwait(false);
        }

        // Deleting the row releases every reference it holds. The edge it was
        // reached by is excluded: that parent row is in the deletion set and is
        // already exclusively locked by this statement.
        CollectReleasedReferences(table, [values], released, statement, cancellationToken, skip: arrivedBy);

        // The incoming references are read after the row and its table are locked,
        // exactly where the recursive walk read them.
        var incoming = IncomingReferences(table, statement, cancellationToken);
        int lastCascade = incoming.Count - 1;
        while (lastCascade >= 0 && CascadeKeys(values, incoming[lastCascade]) is null)
        {
            lastCascade--;
        }

        return new SqlCascadeFrame(table, values, incoming, lastCascade);
    }

    /// <summary>
    /// The transaction-snapshot crosscheck of a cascade (#1370). Under a snapshot fixed at the
    /// transaction's begin (<see cref="IsolationLevel.Snapshot"/> and
    /// <see cref="IsolationLevel.Serializable"/>), a child row the walk reached in latest state
    /// must be a version that snapshot sees. A version it does not see is a write the
    /// transaction never read. Usually its writer committed after the snapshot: the parent row's
    /// exclusive lock waited for every writer of a row referencing it, and an aborted writer's
    /// versions are undone before its locks release. The exception is a statement whose snapshot
    /// holds a parent version that a committed transaction has since replaced: child writers
    /// lock the replacement, not the version this statement locked, so the walk can reach a
    /// child whose writer is still in flight (the referential-locking note above). Either way
    /// deleting that version would overwrite a write the transaction never saw, so the statement
    /// fails with the retryable write-write conflict, first-updater-wins, before it writes
    /// anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PostgreSQL makes the same decision. Its cascade runs with the latest snapshot and, in a
    /// REPEATABLE READ or SERIALIZABLE transaction, the transaction snapshot as a crosscheck
    /// (<c>src/backend/utils/adt/ri_triggers.c:1206-1211</c>, <c>:2810-2815</c>);
    /// <c>heap_delete</c> reports a row the crosscheck does not see as updated
    /// (<c>src/backend/access/heap/heapam.c:2970-2975</c>), and <c>ExecDelete</c> raises that as
    /// a serialization failure, "could not serialize access due to concurrent update"
    /// (<c>src/backend/executor/nodeModifyTable.c:1735-1738</c>). A child inserted after the
    /// snapshot fails the same way. Under <see cref="IsolationLevel.ReadCommitted"/> the
    /// statement reads through a snapshot of its own and the walk deletes the latest version, as
    /// PostgreSQL's read-committed cascade does.
    /// </para>
    /// <para>
    /// Until #1370 the walk deleted the newer version anyway and removed its index entries
    /// through the transaction's snapshot, which does not see it: the index delete matched
    /// nothing, and the deleted child's unique entry stayed live, so its key could never be
    /// inserted again.
    /// </para>
    /// <para>
    /// The walk found the version before it held the row's lock, and a version whose writer was
    /// in flight can be gone by the time the check reads it: the writer rolled back and its undo
    /// deleted the version, or the writer's statement failed and its bracket restored the page
    /// without it. The snapshot never saw such a version either, so the statement fails with the
    /// same conflict. Until the #1390 stress probe found it, the check read the slot with
    /// <c>ReadRow</c>, which throws for a deleted or reverted slot, and the statement failed with
    /// the storage's exception instead: <c>SlottedPageException</c> for a deleted slot,
    /// <c>ArgumentOutOfRangeException</c> for a slot past the restored page's last, or
    /// <c>StorageIOException</c> ("Page N is not allocated") when the undo emptied the page and
    /// freed it. <c>TryReadRecord</c> reports all three as a record that is gone. The race is rare
    /// under the probe's workload (one <c>SlottedPageException</c> in its early runs, none in six
    /// minutes of later ones), so <c>SqlLockWaitTests</c> builds it step by step.
    /// </para>
    /// </remarks>
    /// <param name="table">The child row's table.</param>
    /// <param name="location">The child row's location, exclusively locked by the statement.</param>
    /// <param name="statement">The executing statement.</param>
    /// <param name="action">The referential action named by a snapshot conflict.</param>
    /// <exception cref="TransactionAbortedException">The transaction's snapshot does not see the child version's writer, or the version is gone.</exception>
    /// <exception cref="StorageCorruptionException">The slot holds a record too short for its version stamps (#1362).</exception>
    private void EnsureSnapshotSeesCascadeRow(SqlCatalogTable table, (PageId PageId, int SlotIndex) location, SqlStatementContext statement,
        string action = "CASCADE")
    {
        if (statement.Transaction.IsolationLevel == IsolationLevel.ReadCommitted)
        {
            return;
        }

        statement.Metrics.RecordRowRead();
        if (!_storage.TryReadRecord(location.PageId, location.SlotIndex, table.ObjectId, out var record))
        {
            throw new TransactionAbortedException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorEnsureSnapshotSeesCascadeRowFailure, table.Schema, table.Name, action));
        }

        if (record.Length < SqlRowCodec.StampHeaderSize)
        {
            throw new StorageCorruptionException(
                location.PageId,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EnsureSnapshotSeesCascadeRowFailure2, location.SlotIndex, (long)location.PageId, table.Schema, table.Name, record.Length, SqlRowCodec.StampHeaderSize));
        }

        var (writer, _) = SqlRowCodec.ReadStamps(record.Span);
        if (!statement.Snapshot.IsVisible(writer))
        {
            throw new TransactionAbortedException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorEnsureSnapshotSeesCascadeRowFailure3, table.Schema, table.Name, action, writer));
        }
    }

    /// <summary>
    /// Returns the key a row's children are found by through one incoming
    /// reference, or null when deleting the row cascades nothing through it: the
    /// reference is <c>ON DELETE RESTRICT</c> (checked against the whole deletion
    /// set once the walk is done), or the row's key has a null component and so
    /// matches no child (MATCH SIMPLE).
    /// </summary>
    private static object?[]? CascadeKeys(object?[] values, SqlBoundReference reference)
    {
        if (reference.Constraint.OnDelete != SqlCatalogReferentialAction.Cascade)
        {
            return null;
        }

        var keys = reference.ParentKey.Read(values);
        return keys.Any(value => value is null) ? null : keys;
    }

    /// <summary>
    /// Advances a frame to the next child row its deletion cascades to: the next
    /// match of the current incoming reference that is not already in the deletion
    /// set, reading the next cascading reference's matches when the current one is
    /// exhausted. Returns false when the frame has no more rows to descend into.
    /// </summary>
    /// <remarks>
    /// A reference's matches are read when the walk reaches that reference, after
    /// every row below the previous reference has been collected, and are
    /// materialized: the walk awaits lock acquisitions between them, and the
    /// storage iterator must not stay open across those awaits. The deletion-set
    /// check is made per match, as late as possible, so a row an earlier sibling's
    /// cascade already reached is skipped rather than visited twice.
    /// </remarks>
    private bool TryNextCascadeRow(SqlCascadeFrame frame, Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions,
        SqlStatementContext statement, CancellationToken cancellationToken,
        out SqlBoundReference edge,
        out ((PageId PageId, int SlotIndex) Location, object?[] Values) match)
    {
        while (true)
        {
            if (frame.Matches is { } matches)
            {
                edge = frame.Incoming[frame.Reference];
                while (frame.Match < matches.Count)
                {
                    match = matches[frame.Match++];
                    if (!deletions.ContainsKey((edge.Child.ObjectId, SqlRecordLocation.Pack(match.Location.PageId, match.Location.SlotIndex))))
                    {
                        if (frame.Match == matches.Count)
                        {
                            frame.Matches = null; // the last match: let the list go before its subtree is walked
                        }

                        return true;
                    }
                }

                frame.Matches = null;
            }

            if (++frame.Reference >= frame.Incoming.Count)
            {
                edge = null!;
                match = default;
                return false;
            }

            var reference = frame.Incoming[frame.Reference];
            if (CascadeKeys(frame.Values, reference) is not { } keys)
            {
                continue;
            }

            frame.Matches = CollectConstraintMatches(FindConstraintRows(reference.ChildKey, keys, statement, cancellationToken,
                ConstraintCurrentSnapshot(statement)), statement);
            frame.Match = 0;
        }
    }

    private void ValidateRestrictedDeletes(Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions,
        SqlConstraintUpdates updates, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // Validate against the complete deletion set so physical row order and
        // constraint declaration order cannot change the outcome of one statement.
        // Every row in the set is already exclusively locked by the cascade walk,
        // which is what makes the latest-state child reads below sound.
        foreach (var deletion in deletions.Values)
        {
            foreach (var reference in IncomingReferences(deletion.Table, statement, cancellationToken))
            {
                var (child, constraint) = reference;
                if (constraint.OnDelete is not (SqlCatalogReferentialAction.Restrict or SqlCatalogReferentialAction.NoAction))
                {
                    continue;
                }
                var keys = reference.ParentKey.Read(deletion.Values);
                if (keys.Any(value => value is null))
                {
                    continue;
                }
                foreach (var match in FinalConstraintRows(reference.ChildKey, keys, statement, cancellationToken,
                    deletions, updates, ConstraintCurrentSnapshot(statement)))
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
            await AcquireRowWriteLocksAsync(statement, group.Key, group.Select(row => row.Location).ToList(), cancellationToken, cascade: true).ConfigureAwait(false);
            var hashes = new List<ulong>();
            var indexes = GetLiveIndexes(group.First().Table, statement);
            foreach (var row in group)
            {
                CollectUniqueKeyHashes(indexes, row.Table, row.Values, hashes);
            }

            await AcquireUniqueKeyLocksAsync(statement, group.Key, hashes, cancellationToken, cascade: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Accepts a CHECK predicate a DDL statement declares: every column and collation must
    /// resolve, the predicate must be a deterministic Boolean row expression the evaluator can
    /// run, and it must pass the declaration rules — no casts, no sign over an operand the plan
    /// types as non-numeric, and no function that is not <see cref="SqlFunctionVolatility.Immutable"/>
    /// (owner decision 64 of 2026-10-09: stricter than PostgreSQL and SQLite, so relaxing it later
    /// breaks no stored definition).
    /// </summary>
    /// <param name="expression">The declared predicate.</param>
    /// <param name="table">The table version the predicate constrains.</param>
    /// <param name="defaultCollation">The database default collation.</param>
    /// <param name="functions">The engine's function catalog and the database.</param>
    /// <param name="bindingContext">Whether the named slots are table columns or domain VALUE.</param>
    /// <param name="validateQualifiers">Whether to apply declaration-only qualifier rules; deferred until schema application knows the recorded hash.</param>
    /// <returns>The resolved identities of its function calls, in expression preorder.</returns>
    /// <exception cref="DatabaseException">The predicate does not bind or is not a valid CHECK.</exception>
    internal static IReadOnlyList<string> ValidateCheck(SqlExpression expression, SqlCatalogTable table, Collation defaultCollation,
        SqlFunctionEnvironment functions, SqlBindingContext bindingContext = SqlBindingContext.Table, bool validateQualifiers = true)
    {
        var scope = new SqlExpressionEvaluator(table.Columns, null, defaultCollation: defaultCollation, functions: functions,
            bindingContext: bindingContext);
        SqlPlanner.ValidateExpression(expression, scope);
        new SqlCheckSyntax(table, scope, declaring: true, validateQualifiers).Validate(expression, requireBoolean: true);
        return SqlDefinitionBinder.FunctionCalls(expression, scope);
    }

    /// <summary>
    /// Binds a CHECK predicate the catalog persisted to a table version: every column and
    /// collation must resolve, and the predicate must be a Boolean row expression the evaluator
    /// can run, and every recorded call must still be Immutable. Other declaration rules
    /// <see cref="ValidateCheck"/> adds are not applied again: the
    /// engine that stored the predicate accepted it, and a later engine tightening what DDL
    /// accepts must not make an existing database refuse to open.
    /// </summary>
    /// <param name="expression">The persisted predicate, parsed.</param>
    /// <param name="table">The table version the predicate constrains.</param>
    /// <param name="defaultCollation">The database default collation.</param>
    /// <param name="functions">The engine's function catalog and the database.</param>
    /// <param name="functionCalls">The persisted calls' exact normalized identities in preorder.</param>
    /// <returns>
    /// The predicate compiled to the bound tree every write to the table version evaluates: its
    /// columns as ordinals of the version's rows, its calls bound to their functions and each
    /// comparison's collation fixed, once per table version.
    /// </returns>
    /// <exception cref="DatabaseException">The predicate cannot be evaluated against the table's rows.</exception>
    internal static SqlBoundExpression BindPersistedCheck(SqlExpression expression, SqlCatalogTable table, Collation defaultCollation,
        SqlFunctionEnvironment functions, IReadOnlyList<string> functionCalls, CancellationToken cancellationToken)
    {
        var scope = new SqlExpressionEvaluator(table.Columns, null, defaultCollation: defaultCollation, functions: functions, cancellationToken: cancellationToken);
        SqlDefinitionBinder.PinCalls(expression, scope, functionCalls);
        SqlPersistedExpression.Bind(expression, scope);
        new SqlCheckSyntax(table, scope, declaring: false).Validate(expression, requireBoolean: true);
        return scope.BindPredicate(expression);
    }

    private static string ConstraintName(SqlCatalogTable table, SqlConstraintDefinition definition, int ordinal)
        => SqlConstraintDeclaration.Name(table.Name, definition, ordinal);

    private IReadOnlyList<SqlCatalogConstraint> BindConstraints(SqlCatalogTable table, IReadOnlyList<SqlConstraintDefinition> definitions,
        SortedDictionary<ulong, SqlCatalogTable>? parents = null)
    {
        var result = new List<SqlCatalogConstraint>();
        var names = new HashSet<string>(table.Constraints.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var index in _catalog.GetIndexes(table.ObjectId))
        {
            names.Add(index.Name);
        }

        bool hasPrimaryKeyDeclaration = false;
        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            string name = ConstraintName(table, definition, i);
            if (definition.Kind == SqlConstraintKind.PrimaryKey)
            {
                if (hasPrimaryKeyDeclaration) { throw SqlConstraintDeclaration.MultiplePrimaryKeys(table.Name, name); }
                hasPrimaryKeyDeclaration = true;
            }
            SqlConstraintDeclaration.ValidateColumns(definition, name);
            if (!names.Add(name))
            {
                throw new SqlExecutionException(SqlExecutionException.DuplicateObjectCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ConstraintNameExists, name, table.Name));
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
                var expression = definition.CheckExpression
                    ?? throw new SqlExecutionException(SqlExecutionException.InvalidDefinitionCode, ErrorMessages.CheckPredicateRequired);
                var calls = ValidateCheck(expression, table, _catalog.DefaultCollation, _definitions.Functions);

                // The catalog stores the canonical text of the parsed predicate, not the text as
                // written: no spelling, and no leniency an older parser had for it, reaches
                // storage, and the text is proven to reload to this tree before it is kept.
                string canonical = SqlPersistedExpression.Canonicalize(expression,
                    $"CHECK constraint '{name}' on table '{table.Schema}.{table.Name}'");
                result.Add(new SqlCatalogConstraint(name, SqlCatalogConstraintKind.Check, definition.Columns,
                    checkExpression: canonical, checkFunctionCalls: calls));
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
                throw new SqlExecutionException(SqlExecutionException.UndefinedTableCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyTableMissing, schema, referenced.TableName));
            }
            // Preserve the first binding's exact identity, even if the name disappears
            // before lock acquisition starts. Self-references already hold the target lock.
            if (!self) { parents?.TryAdd(parent.ObjectId, parent); }

            if (definition.Columns.Count != definition.ReferencedColumns.Count || definition.Columns.Count == 0)
            {
                throw new SqlExecutionException(SqlExecutionException.InvalidForeignKeyCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyColumnCount, name));
            }

            for (int column = 0; column < definition.Columns.Count; column++)
            {
                int childOrdinal = FindColumnOrdinal(table, definition.Columns[column]);
                int parentOrdinal = FindColumnOrdinal(parent, definition.ReferencedColumns[column]);
                if (table.Columns[childOrdinal].Type.Type != parent.Columns[parentOrdinal].Type.Type)
                {
                    throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyColumnTypes, name));
                }
                if (table.Columns[childOrdinal].Type.Type is DatabaseType.String or DatabaseType.Character
                    && (table.Columns[childOrdinal].Collation ?? _catalog.DefaultCollation)
                        != (parent.Columns[parentOrdinal].Collation ?? _catalog.DefaultCollation))
                {
                    throw new SqlExecutionException(SqlExecutionException.CollationMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyColumnCollations, name));
                }
            }
            bool unique = (self && SameConstraintColumns(parent.PrimaryKeyColumns, definition.ReferencedColumns)) ||
                _catalog.GetIndexes(parent.ObjectId).Any(index => index.IsUnique && SameConstraintColumns(index.ColumnNames, definition.ReferencedColumns)) ||
                (self && definitions.Any(d => d.Kind is SqlConstraintKind.Unique or SqlConstraintKind.PrimaryKey && SameConstraintColumns(d.Columns, definition.ReferencedColumns)));
            if (!unique)
            {
                throw new SqlExecutionException(SqlExecutionException.InvalidForeignKeyCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ForeignKeyUniqueRequired, name));
            }

            result.Add(new SqlCatalogConstraint(name, SqlCatalogConstraintKind.Reference, definition.Columns, schema, referenced.TableName,
                definition.ReferencedColumns, onDelete: definition.OnDelete switch
                {
                    SqlReferentialAction.Restrict => SqlCatalogReferentialAction.Restrict,
                    SqlReferentialAction.Cascade => SqlCatalogReferentialAction.Cascade,
                    SqlReferentialAction.NoAction => SqlCatalogReferentialAction.NoAction,
                    SqlReferentialAction.SetNull => SqlCatalogReferentialAction.SetNull,
                    SqlReferentialAction.SetDefault => SqlCatalogReferentialAction.SetDefault,
                    _ => throw new SqlExecutionException(SqlExecutionException.InternalErrorCode, ErrorMessages.DeleteActionUnknown),
                }));
        }
        return result;
    }

    /// <summary>
    /// Checks the shape of a CHECK predicate in one pass that visits every node exactly once, so
    /// its cost is linear in the size of the predicate however deeply AND, OR, NOT, COALESCE and
    /// CASE nest. (Walking a Boolean operand once as a plain child and again as a Boolean one
    /// doubled the work at every AND/OR level: a 24-term AND took seconds, and binding at open
    /// ran it for every such table.)
    /// </summary>
    /// <remarks>
    /// A call must resolve to a scalar function of the engine's catalog, or be <c>COALESCE</c>; an
    /// aggregate, or a name outside the catalog, cannot be evaluated against one row. When DDL
    /// declares the predicate, the function must also be <see cref="SqlFunctionVolatility.Immutable"/>
    /// (owner decision 64); a stored predicate is not held to it again, so a function whose
    /// registration changed does not stop its database from opening.
    /// </remarks>
    [global::System.Diagnostics.DebuggerDisplay("SqlCheckSyntax")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.SqlCheckSyntax.DebugView))]
    private sealed class SqlCheckSyntax
    {
        private readonly SqlCatalogTable _table;
        private readonly SqlExpressionEvaluator _scope;
        private readonly bool _declaring;
        private readonly bool _validateQualifiers;

        /// <summary>Initializes the check of one predicate.</summary>
        /// <param name="table">The table version the predicate constrains.</param>
        /// <param name="scope">The scope its calls resolve in.</param>
        /// <param name="declaring">
        /// <see langword="true"/> when DDL accepts the predicate, which also applies the declaration
        /// rules against casts and functions that are not immutable; <see langword="false"/> when
        /// the catalog's persisted predicate binds.
        /// </param>
        /// <param name="validateQualifiers">Whether to enforce qualifier identity for a new or changed schema.</param>
        internal SqlCheckSyntax(SqlCatalogTable table, SqlExpressionEvaluator scope, bool declaring, bool validateQualifiers = true)
        {
            _table = table;
            _scope = scope;
            _declaring = declaring;
            _validateQualifiers = validateQualifiers;
        }

        /// <summary>Checks one node.</summary>
        /// <param name="expression">The node to check.</param>
        /// <param name="requireBoolean">Whether the node's position requires a Boolean value.</param>
        /// <exception cref="DatabaseException">The node breaks a CHECK rule.</exception>
        internal void Validate(SqlExpression expression, bool requireBoolean)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlParameterExpression or SqlSubqueryExpression or SqlExistsExpression or SqlStarExpression
                or SqlInExpression { Subquery: not null } || (_declaring && expression is SqlCastExpression))
            {
                throw new SqlExecutionException(expression is SqlSubqueryExpression or SqlExistsExpression or SqlInExpression { Subquery: not null }
                    ? SqlExecutionException.UnsupportedCode : SqlExecutionException.InvalidDefinitionCode, ErrorMessages.CheckExpressionInvalid);
            }

            // Declaration-only qualifier check: persisted predicates retain the older binding
            // contract. Sql.Language owns AST construction, so use the catalog's identity here
            // without manufacturing a relation node or widening the language boundary.
            if (_declaring && _validateQualifiers && expression is SqlColumnReferenceExpression { TableAlias: not null } qualifiedColumn &&
                (!string.Equals(qualifiedColumn.TableAlias, _table.Name, StringComparison.OrdinalIgnoreCase) ||
                 qualifiedColumn.SchemaName is not null && !string.Equals(qualifiedColumn.SchemaName, _table.Schema, StringComparison.OrdinalIgnoreCase)))
            {
                string name = string.Join('.', new[] { qualifiedColumn.SchemaName, qualifiedColumn.TableAlias, qualifiedColumn.ColumnName }.Where(part => part is not null));
                throw new SqlPlanningException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.MissingFromClauseEntry, name), qualifiedColumn.Location);
            }

            // A call's arguments were matched against its overloads when the statement was planned
            // (DDL) or the persisted predicate was bound (open); here only its function is checked.
            var called = expression is SqlFunctionCallExpression function ? CheckFunction(function) : null;

            // Each child is visited once, with the requirement its position puts on it: the operands
            // of AND, OR and NOT are Boolean, and so are COALESCE's arguments and a CASE's results
            // (and a searched CASE's conditions) when the call or CASE itself must be Boolean.
            switch (expression)
            {
                case SqlLogicalExpression logical:
                    // Every term of an AND/OR chain at one level, however long the chain (#1151).
                    foreach (var term in logical.Operands)
                    {
                        Validate(term, requireBoolean: true);
                    }
                    break;
                case SqlUnaryExpression { Operator: SqlUnaryOperator.Not } negation:
                    Validate(negation.Operand, requireBoolean: true);
                    break;
                case SqlFunctionCallExpression call:
                    bool booleanArguments = requireBoolean && called is null;
                    foreach (var argument in call.Arguments)
                    {
                        Validate(argument, booleanArguments);
                    }
                    break;
                case SqlCaseExpression caseExpression:
                    if (caseExpression.Input is not null)
                    {
                        Validate(caseExpression.Input, requireBoolean: false);
                    }
                    foreach (var branch in caseExpression.WhenClauses)
                    {
                        Validate(branch.Condition, requireBoolean && caseExpression.Input is null);
                        Validate(branch.Result, requireBoolean);
                    }
                    if (caseExpression.ElseResult is not null)
                    {
                        Validate(caseExpression.ElseResult, requireBoolean);
                    }
                    break;
                default:
                    foreach (var child in SqlPlanner.Children(expression))
                    {
                        Validate(child, requireBoolean: false);
                    }
                    break;
            }

            CheckApplicationResults(expression);
            if (!requireBoolean)
            {
                return;
            }
            bool boolean = expression switch
            {
                SqlLogicalExpression => true,
                SqlBinaryExpression binary => binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or SqlBinaryOperator.LessThan or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterOrEqual,
                SqlUnaryExpression unary => unary.Operator == SqlUnaryOperator.Not,
                SqlDistinctExpression or SqlBooleanTestExpression or SqlIsNullExpression or SqlBetweenExpression or SqlInExpression or SqlLikeExpression => true,
                SqlLiteralExpression literal => literal.LiteralType is SqlLiteralType.Boolean or SqlLiteralType.Null
                    || SqlExpressionEvaluator.TryBooleanLiteral(literal, out _),
                SqlColumnReferenceExpression column => _table.Columns[FindColumnOrdinal(_table, column.ColumnName)].Type.Type == DatabaseType.Boolean,
                // COALESCE over Boolean arguments, or a function declared to return BOOLEAN.
                SqlFunctionCallExpression => called is null || called.ReturnType.Storage.Type == DatabaseType.Boolean,
                SqlCaseExpression => true,
                _ => false,
            };
            if (!boolean)
            {
                if (!_declaring && expression is SqlFunctionCallExpression call && called is not null && !SqlStandardLibrary.Contains(called))
                {
                    throw new SqlFunctionResultMismatchException(call, ResultName(call, called), "BOOLEAN");
                }

                throw SqlEvaluationException.BooleanRequired("CHECK", SqlType.NameOf(_scope.StaticTypeOf(expression))).At(expression.Location);
            }
        }

        /// <summary>
        /// Checks that an application function's result fits where the predicate uses it: an
        /// operand of a comparison, <c>BETWEEN</c> or <c>IN</c> list of a type its other operand
        /// compares with, and arithmetic operands accepted by the numeric/temporal planner rules. The evaluator would refuse
        /// either on every row the call returns a value for. Calls of the standard library, and
        /// operands whose type the plan cannot tell, are not checked, so no predicate DDL accepted
        /// before is refused.
        /// </summary>
        /// <exception cref="SqlFunctionResultMismatchException">An application function's result does not fit.</exception>
        private void CheckApplicationResults(SqlExpression expression)
        {
            switch (expression)
            {
                case SqlBinaryExpression binary when binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
                    or SqlBinaryOperator.LessThan or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterOrEqual:
                    CheckComparable(binary.Left, binary.Right);
                    break;
                case SqlBinaryExpression binary when binary.Operator is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract
                    or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo:
                    SqlPlanner.ValidateTemporalArithmetic(binary, _scope);
                    var leftType = _scope.StaticTypeOf(binary.Left);
                    var rightType = _scope.StaticTypeOf(binary.Right);
                    if (!SqlTemporalArithmetic.IsTemporal(leftType) && !SqlTemporalArithmetic.IsTemporal(rightType))
                    {
                        CheckNumber(binary.Left);
                        CheckNumber(binary.Right);
                    }
                    break;
                case SqlDistinctExpression distinct:
                    CheckComparable(distinct.Left, distinct.Right);
                    break;
                case SqlBetweenExpression between:
                    CheckComparable(between.Operand, between.Low);
                    CheckComparable(between.Operand, between.High);
                    break;
                case SqlInExpression { Values: { } values } list:
                    foreach (var value in values)
                    {
                        CheckComparable(list.Operand, value);
                    }
                    break;
            }
        }

        private void CheckComparable(SqlExpression left, SqlExpression right)
        {
            var (leftCall, leftType) = Operand(left);
            var (rightCall, rightType) = Operand(right);
            if (leftCall is null && rightCall is null || leftType == DatabaseType.Null || rightType == DatabaseType.Null ||
                Comparable(leftType, rightType))
            {
                return;
            }

            var (call, type, other) = leftCall is not null ? (leftCall, leftType, rightType) : (rightCall!, rightType, leftType);
            throw new SqlFunctionResultMismatchException(call, SqlType.NameOf(type), $"a value that compares with {SqlType.NameOf(other)}");

            // The value order's own rule (SqlValueComparer): the same type, two numbers or two character
            // types. JSON is read as text and JSONB as bytes.
            static bool Comparable(DatabaseType first, DatabaseType second)
            {
                first = first switch { DatabaseType.Character or DatabaseType.Json => DatabaseType.String, DatabaseType.JsonBinary => DatabaseType.Binary, _ => first };
                second = second switch { DatabaseType.Character or DatabaseType.Json => DatabaseType.String, DatabaseType.JsonBinary => DatabaseType.Binary, _ => second };
                return first == second || IsNumber(first) && IsNumber(second) || SqlTemporalArithmetic.IsCalendar(first) && SqlTemporalArithmetic.IsCalendar(second);
            }
        }

        private void CheckNumber(SqlExpression operand)
        {
            var (call, type) = Operand(operand);
            if (call is not null && type != DatabaseType.Null && !IsNumber(type))
            {
                throw new SqlFunctionResultMismatchException(call, SqlType.NameOf(type), "a number");
            }
        }

        /// <summary>
        /// An operand's static type, and the call when the operand is a call of an application
        /// function. Only a call, a literal or a column is typed; anything else is not checked.
        /// </summary>
        private (SqlFunctionCallExpression? Call, DatabaseType Type) Operand(SqlExpression expression)
        {
            while (expression is SqlCollateExpression collate)
            {
                expression = collate.Operand;
            }

            switch (expression)
            {
                case SqlFunctionCallExpression call when !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName):
                    return _scope.ResolveFunction(call) is SqlScalarFunction function && !SqlStandardLibrary.Contains(function)
                        ? (call, _scope.StaticCallType(call))
                        : (null, DatabaseType.Null);
                case SqlLiteralExpression or SqlColumnReferenceExpression:
                    return (null, _scope.StaticTypeOf(expression));
                default:
                    return (null, DatabaseType.Null);
            }
        }

        private static bool IsNumber(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32
            or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal;

        /// <summary>A call's result type as a message names it: its static type, else its declared type.</summary>
        private string ResultName(SqlFunctionCallExpression call, SqlScalarFunction function)
            => _scope.StaticCallType(call) is var type && type != DatabaseType.Null ? SqlType.NameOf(type) : function.ReturnType.Name;

        /// <summary>
        /// Checks that a call may appear in a CHECK, and returns the scalar function it resolves to;
        /// null for <c>COALESCE</c>, the special form.
        /// </summary>
        /// <exception cref="DatabaseException">
        /// The call is an aggregate's or names no function of the catalog, or DDL declares a call of a
        /// function that is not immutable.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private SqlScalarFunction? CheckFunction(SqlFunctionCallExpression call)
        {
            if (SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
            {
                return null;
            }

            if (_scope.ResolveFunction(call) is not SqlScalarFunction function)
            {
                throw new SqlExecutionException(SqlExecutionException.UnsupportedCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CheckFunctionUnsupported, call.FunctionName));
            }
            if (function.Volatility != SqlFunctionVolatility.Immutable)
            {
                throw new SqlExecutionException(SqlExecutionException.InvalidDefinitionCode,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CheckFunctionVolatility, call.FunctionName, (function.Volatility == SqlFunctionVolatility.Stable ? "STABLE" : "VOLATILE")));
            }
            if (function is SqlExpressionFunction { Unresolved: { } reason })
            {
                throw new SqlPlanningException(SqlEvaluationException.UnregisteredFunctionCode, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.SqlFunctionUnresolved, function.Name, reason), call.Location);
            }

            return function;
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.SqlCheckSyntax value)
            {
                _ = value;
            }
        }
    }
}
