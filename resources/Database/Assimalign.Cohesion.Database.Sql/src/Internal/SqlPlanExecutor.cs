using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Executes bound plans against shared storage: table scans with predicate
/// filtering, projection/sort/limit for SELECT, typed writes for DML, and catalog
/// calls for DDL. Results are deterministic: scans yield rows in physical order and
/// ORDER BY sorts are stable.
/// </summary>
internal sealed class SqlPlanExecutor
{
    private readonly SqlStorage _storage;
    private readonly ISqlCatalog _catalog;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    internal SqlPlanExecutor(SqlStorage storage, ISqlCatalog catalog, IReadOnlyDictionary<string, object?>? parameters)
    {
        _storage = storage;
        _catalog = catalog;
        _parameters = parameters;
    }

    internal async Task<QueryResult> ExecuteAsync(SqlPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        switch (plan)
        {
            case SqlSelectPlan select:
                return ExecuteSelect(select, statement, cancellationToken);
            case SqlInsertPlan insert:
                return await ExecuteInsertAsync(insert, statement, cancellationToken).ConfigureAwait(false);
            case SqlUpdatePlan update:
                return await ExecuteUpdateAsync(update, statement, cancellationToken).ConfigureAwait(false);
            case SqlDeletePlan delete:
                return await ExecuteDeleteAsync(delete, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateTablePlan create:
                return await ExecuteCreateTableAsync(create, cancellationToken).ConfigureAwait(false);
            case SqlDropTablePlan drop:
                return await ExecuteDropTableAsync(drop, statement, cancellationToken).ConfigureAwait(false);
            case SqlAddColumnPlan addColumn:
                await AcquireObjectLockAsync(statement, addColumn.Schema, addColumn.Name, LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                await _catalog.AddColumnAsync(addColumn.Schema, addColumn.Name, addColumn.Column, cancellationToken).ConfigureAwait(false);
                return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
            case SqlDropColumnPlan dropColumn:
                return await ExecuteDropColumnAsync(dropColumn, statement, cancellationToken).ConfigureAwait(false);
            default:
                throw new DatabaseException($"Plan {plan.GetType().Name} is not executable.");
        }
    }

    // ── SELECT ─────────────────────────────────────────────────────────

    private QueryResult ExecuteSelect(SqlSelectPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var matches = new List<object?[]>();

        foreach (var (_, values) in Scan(plan.Table, statement, cancellationToken))
        {
            if (evaluator.Matches(plan.Where, values))
            {
                matches.Add(values);
            }
        }

        if (plan.IsCountStar)
        {
            var countColumns = new[] { new QueryColumn { Name = plan.Projections[0].Name, Ordinal = 0, Type = DatabaseType.Int64 } };
            return new SqlMaterializedResultSet(countColumns, new List<object?[]> { new object?[] { (long)matches.Count } });
        }

        // ORDER BY before projection so sort keys may reference any table column.
        if (plan.OrderBy.Count > 0)
        {
            matches = SortRows(matches, plan.OrderBy, evaluator);
        }

        // Project.
        var projected = new List<object?[]>(matches.Count);
        foreach (var row in matches)
        {
            var output = new object?[plan.Projections.Count];

            for (int i = 0; i < plan.Projections.Count; i++)
            {
                var projection = plan.Projections[i];
                output[i] = projection.ColumnOrdinal is int ordinal
                    ? row[ordinal]
                    : evaluator.Evaluate(projection.Expression!, row);
            }

            projected.Add(output);
        }

        if (plan.IsDistinct)
        {
            projected = Deduplicate(projected);
        }

        // OFFSET / LIMIT.
        IEnumerable<object?[]> window = projected;
        if (plan.Offset is long offset)
        {
            window = window.Skip((int)offset);
        }
        if (plan.Limit is long limit)
        {
            window = window.Take((int)limit);
        }

        var columns = new QueryColumn[plan.Projections.Count];
        for (int i = 0; i < plan.Projections.Count; i++)
        {
            columns[i] = new QueryColumn { Name = plan.Projections[i].Name, Ordinal = i, Type = plan.Projections[i].Type };
        }

        return new SqlMaterializedResultSet(columns, window.ToList());
    }

    private static List<object?[]> SortRows(List<object?[]> rows, IReadOnlyList<SqlOrderByColumn> orderBy, SqlExpressionEvaluator evaluator)
    {
        // Precompute sort keys; OrderBy is a stable sort, satisfying determinism.
        var keyed = rows.Select(row => (Row: row, Keys: orderBy.Select(o => evaluator.Evaluate(o.Expression, row)).ToArray()));

        IOrderedEnumerable<(object?[] Row, object?[] Keys)>? ordered = null;

        for (int i = 0; i < orderBy.Count; i++)
        {
            int index = i;
            var comparer = Comparer<object?>.Create(CompareNullable);

            if (ordered is null)
            {
                ordered = orderBy[index].IsDescending
                    ? keyed.OrderByDescending(x => x.Keys[index], comparer)
                    : keyed.OrderBy(x => x.Keys[index], comparer);
            }
            else
            {
                ordered = orderBy[index].IsDescending
                    ? ordered.ThenByDescending(x => x.Keys[index], comparer)
                    : ordered.ThenBy(x => x.Keys[index], comparer);
            }
        }

        return ordered!.Select(x => x.Row).ToList();

        static int CompareNullable(object? left, object? right) => (left, right) switch
        {
            (null, null) => 0,
            (null, _) => -1, // nulls first
            (_, null) => 1,
            _ => SqlExpressionEvaluator.Compare(left, right),
        };
    }

    private static List<object?[]> Deduplicate(List<object?[]> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<object?[]>(rows.Count);

        foreach (var row in rows)
        {
            // A textual key is sufficient for dedup: values are typed scalars with
            // invariant, prefix-free formatting per field.
            string key = string.Join('\u0001', row.Select(static v =>
                v is null ? "" : $"{v.GetType().Name}:{Convert.ToString(v, CultureInfo.InvariantCulture)}"));

            if (seen.Add(key))
            {
                result.Add(row);
            }
        }

        return result;
    }

    // ── DML ────────────────────────────────────────────────────────────
    //
    // Write statements execute in two phases (area DESIGN §3.8 step 3):
    //
    //   Phase 1 (no physical bracket): scan through the statement snapshot,
    //   collect targets, and acquire locks through the lock manager — an
    //   IntentExclusive lock on the table, then an Exclusive lock per target
    //   row (keyed by the version's packed location, the same identity the
    //   version-store ledger uses). Lock waits are asynchronous, honor the
    //   session's cancellation token, and are where deadlocks are detected
    //   (the requester whose wait would close a cycle aborts).
    //
    //   Phase 2 (the coordinator's gated apply bracket): re-validate each
    //   target against its CURRENT stamps — the latest-state check under the
    //   exclusive lock, the B+Tree uniqueness-discipline precedent; a
    //   snapshot-only check here would admit write skew. A target tombstoned
    //   by a concurrently COMMITTED transaction fails the statement with a
    //   retryable write-write conflict (first-updater-wins); a tombstone from
    //   a transaction that rolled back was already cleared by its undo before
    //   its locks released, so the re-validation passes. Then apply: inserts
    //   and new update-versions stamp the writer, tombstones stamp the
    //   deleter, and every effect is recorded in the version-store ledger so
    //   rollback can undo it logically.

    private async Task<QueryResult> ExecuteInsertAsync(SqlInsertPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var rows = new List<byte[]>();

        foreach (var valueRow in plan.Rows)
        {
            var values = new object?[plan.Table.Columns.Count];
            var assigned = new bool[plan.Table.Columns.Count];

            for (int i = 0; i < plan.TargetOrdinals.Count; i++)
            {
                int ordinal = plan.TargetOrdinals[i];
                values[ordinal] = CoerceForColumn(evaluator.Evaluate(valueRow[i], Array.Empty<object?>()), plan.Table.Columns[ordinal]);
                assigned[ordinal] = true;
            }

            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                if (!assigned[ordinal])
                {
                    values[ordinal] = ResolveDefault(plan.Table.Columns[ordinal]);
                }
            }

            rows.Add(SqlRowCodec.Encode(plan.Table.ObjectId, plan.Table.Columns, values, statement.Transaction.Sequence));
        }

        // Inserts need no row locks (the rows do not exist yet); the intent
        // lock coordinates with table-grain DDL.
        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(plan.Table.ObjectId), LockMode.IntentExclusive, cancellationToken).ConfigureAwait(false);

        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, bracket =>
        {
            foreach (byte[] row in rows)
            {
                var (pageId, slotIndex) = _storage.InsertRow(bracket, row);
                statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, plan.Table.ObjectId, pageId, slotIndex);
            }

            return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, rows.Count);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResult> ExecuteUpdateAsync(SqlUpdatePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var targets = new List<(PageId PageId, int SlotIndex, object?[] Values)>();

        foreach (var (location, values) in Scan(plan.Table, statement, cancellationToken))
        {
            if (evaluator.Matches(plan.Where, values))
            {
                targets.Add((location.PageId, location.SlotIndex, values));
            }
        }

        var replacements = new List<(PageId PageId, int SlotIndex, byte[] NewVersion)>(targets.Count);

        foreach (var (pageId, slotIndex, values) in targets)
        {
            var updated = (object?[])values.Clone();

            foreach (var (ordinal, expression) in plan.Assignments)
            {
                // Assignments evaluate against the ORIGINAL row (SQL semantics).
                updated[ordinal] = CoerceForColumn(evaluator.Evaluate(expression, values), plan.Table.Columns[ordinal]);
            }

            replacements.Add((pageId, slotIndex,
                SqlRowCodec.Encode(plan.Table.ObjectId, plan.Table.Columns, updated, statement.Transaction.Sequence)));
        }

        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.ConvertAll(t => (t.PageId, t.SlotIndex)), cancellationToken).ConfigureAwait(false);

        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, bracket =>
        {
            foreach (var (pageId, slotIndex, newVersion) in replacements)
            {
                EnsureLatestVersion(plan.Table, pageId, slotIndex, statement.Transaction.Sequence);

                // MVCC update = version chain in the record space: tombstone the
                // old version in place (same-length write — never relocates) and
                // insert the new version, both stamped with this transaction's
                // sequence.
                TombstoneVersion(statement, bracket, plan.Table.ObjectId, pageId, slotIndex);
                var location = _storage.InsertRow(bracket, newVersion);
                statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, plan.Table.ObjectId, location.PageId, location.SlotIndex);
            }

            return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, replacements.Count);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResult> ExecuteDeleteAsync(SqlDeletePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var targets = new List<(PageId PageId, int SlotIndex)>();

        foreach (var (location, values) in Scan(plan.Table, statement, cancellationToken))
        {
            if (evaluator.Matches(plan.Where, values))
            {
                targets.Add(location);
            }
        }

        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets, cancellationToken).ConfigureAwait(false);

        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, bracket =>
        {
            foreach (var (pageId, slotIndex) in targets)
            {
                EnsureLatestVersion(plan.Table, pageId, slotIndex, statement.Transaction.Sequence);

                // Tombstone, not slot removal: older snapshots must keep seeing
                // the row until the purge worker reclaims versions below every
                // live snapshot's horizon.
                TombstoneVersion(statement, bracket, plan.Table.ObjectId, pageId, slotIndex);
            }

            return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, targets.Count);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase-one lock acquisition for a write statement: IntentExclusive on the
    /// table, then Exclusive per target row in deterministic (location) order.
    /// Locks belong to the transaction and release as a set at commit/rollback
    /// (two-phase locking); deadlocks surface here as the lock manager's
    /// requester-closes-cycle abort.
    /// </summary>
    private static async ValueTask AcquireRowWriteLocksAsync(
        SqlStatementContext statement,
        ulong objectId,
        List<(PageId PageId, int SlotIndex)> targets,
        CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var owner = statement.Transaction.Sequence;

        await locks.AcquireAsync(owner, LockResource.Object(objectId), LockMode.IntentExclusive, cancellationToken).ConfigureAwait(false);

        var keys = targets.ConvertAll(target => SqlRecordLocation.Pack(target.PageId, target.SlotIndex));
        keys.Sort();

        foreach (ulong key in keys)
        {
            await locks.AcquireAsync(owner, LockResource.Entry(objectId, key), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The latest-state check under the exclusive row lock: a target tombstoned
    /// by a concurrently committed transaction fails the statement with a
    /// retryable write-write conflict — first-updater-wins. (A snapshot
    /// visibility check alone would admit write skew; this is the B+Tree
    /// uniqueness-discipline precedent applied to row updates.)
    /// </summary>
    /// <exception cref="TransactionAbortedException">The row was modified by a concurrently committed transaction.</exception>
    private void EnsureLatestVersion(SqlCatalogTable table, PageId pageId, int slotIndex, TransactionSequence self)
    {
        var record = _storage.ReadRow(pageId, slotIndex);

        if (record.Length < SqlRowCodec.StampHeaderSize)
        {
            throw new TransactionAbortedException(
                $"Write-write conflict on '{table.Schema}.{table.Name}': the target row version was reclaimed by a concurrent transaction. Retry the transaction.");
        }

        var (_, deleter) = SqlRowCodec.ReadStamps(record.Span);

        if (deleter != TransactionSequence.None && deleter != self)
        {
            throw new TransactionAbortedException(
                $"Write-write conflict on '{table.Schema}.{table.Name}': the row was modified by concurrently committed transaction {deleter} (first-updater-wins). Retry the transaction.");
        }
    }

    /// <summary>
    /// Stamps the record's deleter with the statement's transaction sequence —
    /// the same-length in-place tombstone write — and records it in the
    /// version-store ledger for logical undo and pruning.
    /// </summary>
    private void TombstoneVersion(SqlStatementContext statement, IStorageTransaction bracket, ulong objectId, PageId pageId, int slotIndex)
    {
        var current = _storage.ReadRow(pageId, slotIndex);
        byte[] tombstoned = SqlRowCodec.WithDeleter(current.Span, statement.Transaction.Sequence);
        _storage.UpdateRow(bracket, pageId, slotIndex, tombstoned);
        statement.Coordinator.VersionStore.RecordTombstoned(statement.Transaction.Sequence, objectId, pageId, slotIndex);
    }

    // ── DDL ────────────────────────────────────────────────────────────

    private async Task<QueryResult> ExecuteCreateTableAsync(SqlCreateTablePlan plan, CancellationToken cancellationToken)
    {
        if (plan.IfNotExists && _catalog.TryGetTable(plan.Schema, plan.Name, out _))
        {
            return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }

        await _catalog.CreateTableAsync(plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey, cancellationToken).ConfigureAwait(false);
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    /// <summary>
    /// Drops a column and rewrites the table's rows to the new positional layout —
    /// row records are positional, so removing a middle column requires splicing
    /// every stored row (ADD COLUMN, by contrast, is O(1): missing trailing
    /// components decode as null). Runs under the table's Exclusive lock: the
    /// intent-lock matrix makes the rewrite wait for in-flight row writers (and
    /// them for it), so no writer's uncommitted version can be rewritten from
    /// under it.
    /// </summary>
    private async Task<QueryResult> ExecuteDropColumnAsync(SqlDropColumnPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetTable(plan.Schema, plan.Name, out var before))
        {
            throw new DatabaseException($"Table '{plan.Schema}.{plan.Name}' does not exist.");
        }

        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(before.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        int droppedOrdinal = -1;
        for (int i = 0; i < before.Columns.Count; i++)
        {
            if (string.Equals(before.Columns[i].Name, plan.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                droppedOrdinal = i;
                break;
            }
        }

        // The rewrite walks EVERY stored version — visible or not — because the
        // whole record space must stay decodable on the new positional layout;
        // stamps are preserved so visibility is unchanged by the DDL.
        var targets = new List<(PageId PageId, int SlotIndex, object?[] Values, TransactionSequence Writer, TransactionSequence Deleter)>();
        if (droppedOrdinal >= 0)
        {
            foreach (var (location, values, writer, deleter) in ScanVersions(before, cancellationToken))
            {
                targets.Add((location.PageId, location.SlotIndex, values, writer, deleter));
            }
        }

        var updated = await _catalog.DropColumnAsync(plan.Schema, plan.Name, plan.ColumnName, cancellationToken).ConfigureAwait(false);

        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, bracket =>
        {
            foreach (var (pageId, slotIndex, values, writer, deleter) in targets)
            {
                var spliced = new object?[values.Length - 1];
                for (int i = 0, j = 0; i < values.Length; i++)
                {
                    if (i != droppedOrdinal)
                    {
                        spliced[j++] = values[i];
                    }
                }

                byte[] record = SqlRowCodec.Encode(updated.ObjectId, updated.Columns, spliced, writer);

                if (deleter != TransactionSequence.None)
                {
                    record = SqlRowCodec.WithDeleter(record, deleter);
                }

                try
                {
                    _storage.UpdateRow(bracket, pageId, slotIndex, record);
                }
                catch (SlottedPageException)
                {
                    _storage.DeleteRow(bracket, pageId, slotIndex);
                    _storage.InsertRow(bracket, record);
                }
            }

            return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResult> ExecuteDropTableAsync(SqlDropTablePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetTable(plan.Schema, plan.Name, out var table))
        {
            if (plan.IfExists)
            {
                return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
            }

            await _catalog.DropTableAsync(plan.Schema, plan.Name, cancellationToken).ConfigureAwait(false);
            return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }

        // The DDL-vs-writer interlock: an Exclusive table lock waits for every
        // in-flight row writer (IntentExclusive holders) to finish before the
        // table is dropped — and blocks new ones until this transaction ends.
        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        await _catalog.DropTableAsync(plan.Schema, plan.Name, cancellationToken).ConfigureAwait(false);
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    /// <summary>
    /// Acquires a table-grain lock for a DDL statement by schema-qualified name.
    /// </summary>
    private async ValueTask AcquireObjectLockAsync(SqlStatementContext statement, string schema, string name, LockMode mode, CancellationToken cancellationToken)
    {
        if (_catalog.TryGetTable(schema, name, out var table))
        {
            await statement.Coordinator.LockManager.AcquireAsync(
                statement.Transaction.Sequence, LockResource.Object(table.ObjectId), mode, cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Scan + coercion helpers ────────────────────────────────────────

    /// <summary>
    /// Scans the table's visible row versions through the statement's snapshot:
    /// a version is visible when its writer is admitted and its deleter (when
    /// stamped) is not — a visible tombstone reads as absence. Exactly one
    /// version of a logical row is visible per snapshot by construction: an
    /// update stamps the old version's deleter with the same sequence that wrote
    /// the new version.
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> Scan(
        SqlCatalogTable table,
        SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var snapshot = statement.Snapshot;

        foreach (var version in ScanVersions(table, cancellationToken))
        {
            if (!snapshot.IsVisible(version.Writer))
            {
                continue;
            }

            if (version.Deleter != TransactionSequence.None && snapshot.IsVisible(version.Deleter))
            {
                continue; // a visible tombstone reads as absence
            }

            yield return (version.Location, version.Values);
        }
    }

    /// <summary>
    /// Scans every stored version of the table's rows — visible or not — with
    /// its stamps. DDL row rewrites use this: the whole record space must stay
    /// decodable across a layout change, so tombstoned and concurrent versions
    /// rewrite too, stamps preserved.
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values, TransactionSequence Writer, TransactionSequence Deleter)> ScanVersions(
        SqlCatalogTable table,
        CancellationToken cancellationToken)
    {
        using var iterator = _storage.GetUnitIterator();

        while (iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unit = iterator.Current;
            var values = SqlRowCodec.TryDecode(unit.Data.Span, table.ObjectId, table.Columns.Count, out var writer, out var deleter);

            if (values is not null)
            {
                yield return ((unit.PageId, unit.SlotIndex), values, writer, deleter);
            }
        }
    }

    /// <summary>
    /// Coerces an evaluated value to a column's storage type, enforcing nullability.
    /// </summary>
    internal static object? CoerceForColumn(object? value, SqlCatalogColumn column)
    {
        if (value is null)
        {
            if (!column.IsNullable)
            {
                throw new DatabaseException($"Column '{column.Name}' does not allow NULL.");
            }

            return null;
        }

        try
        {
            return column.Type.Type switch
            {
                DatabaseType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                DatabaseType.Int8 => Convert.ToSByte(value, CultureInfo.InvariantCulture),
                DatabaseType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                DatabaseType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                DatabaseType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                DatabaseType.Float32 => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                DatabaseType.Float64 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                DatabaseType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                DatabaseType.String or DatabaseType.Json => value as string
                    ?? Convert.ToString(value, CultureInfo.InvariantCulture)!,
                DatabaseType.Binary or DatabaseType.JsonBinary => value is byte[] bytes
                    ? bytes
                    : throw new DatabaseException($"Column '{column.Name}' requires binary data."),
                DatabaseType.Date => value switch
                {
                    DateOnly date => date,
                    string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                    DateTime dateTime => DateOnly.FromDateTime(dateTime),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to DATE."),
                },
                DatabaseType.Time => value switch
                {
                    TimeOnly time => time,
                    string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to TIME."),
                },
                DatabaseType.DateTime => value switch
                {
                    DateTime dateTime => dateTime,
                    string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to TIMESTAMP."),
                },
                DatabaseType.DateTimeOffset => value switch
                {
                    DateTimeOffset dateTimeOffset => dateTimeOffset,
                    string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to TIMESTAMPTZ."),
                },
                DatabaseType.TimeSpan => value switch
                {
                    TimeSpan timeSpan => timeSpan,
                    string text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to INTERVAL."),
                },
                DatabaseType.Guid => value switch
                {
                    Guid guid => guid,
                    string text => Guid.Parse(text),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to UUID."),
                },
                _ => throw new DatabaseException($"Column type {column.Type.Type} cannot store values yet."),
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw new DatabaseException(
                $"Cannot convert value '{value}' to column '{column.Name}' of type {column.Type.Type}.", exception);
        }
    }

    private static object? ResolveDefault(SqlCatalogColumn column)
    {
        if (column.DefaultLiteral is null)
        {
            if (!column.IsNullable)
            {
                throw new DatabaseException($"Column '{column.Name}' does not allow NULL and has no default.");
            }

            return null;
        }

        return CoerceForColumn(column.DefaultLiteral, column);
    }
}
