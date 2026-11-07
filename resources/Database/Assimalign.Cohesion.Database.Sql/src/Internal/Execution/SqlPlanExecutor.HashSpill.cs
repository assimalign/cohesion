using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action<string, long>? ObserveHashSpill { get => HashSpill.Observe; set => HashSpill.Observe = value; }
    internal static Func<int, int>? HashSpillPartitionHash { get => HashSpill.PartitionHash; set => HashSpill.PartitionHash = value; }

    private async Task<DatabaseResult> ExecuteStagedHashGroupAsync(SqlGroupPlan plan, SqlStatementContext statement,
        CancellationToken token, SqlDatabaseSession owner)
    {
        // Match the staged reference: finish WHERE/ON before the first volatile key,
        // FILTER or argument. Pass-through source projections contain no user calls.
        var rows = new List<object?[]>();
        SqlSelectMemory memory = default;
        DatabaseResult? result = null;
        Exception? failure = null;
        SqlStagedSortSource? source = null;
        try
        {
            await foreach (var row in EnumerateGroupInputAsync(plan, statement, token).ConfigureAwait(false))
            {
                source = SqlStagedSortSource.For(rows);
                if (source is not null) { source.Append(row, token); continue; }
                try { RetainSelectRow(ref memory, row); rows.Add(row); }
                catch (SqlExecutionException exception) when (exception.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
                { SqlStagedSortSource.Add(rows, row, ref memory, _metrics!, _definitions.StatementMemoryBudget, owner, token); }
            }
            source = SqlStagedSortSource.For(rows);
            var columns = ((DatabaseResultSet)MaterializeProjected(((SqlRelationPlan)plan.Input).OutputProjections, [])).Columns;
            ReleaseSelectMemory(ref memory);
            DatabaseResultSet input;
            if (source is null) { input = new SqlMaterializedResultSet(columns, rows); }
            else { source.Seal(token); input = new HashGroupInput(columns, source); }
            result = await ExecuteMaterializedPlainGroupAsync(plan, statement, token, owner, input, source?.Statement).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            ReleaseSelectMemory(ref memory);
            source ??= SqlStagedSortSource.For(rows);
            // A hash spill can hand this SAME quota owner to the final result.
            if (source is not null && (result is not SqlSpilledResultSet spilled || !spilled.OwnsStatement(source.Statement)))
            {
                try { source.Statement.Dispose(); }
                catch (AggregateException cleanup)
                {
                    var translated = SqlSpilledResultSet.TranslateCleanup(cleanup);
                    if (failure is null) { throw translated; }
                    failure.Data[nameof(HashGroupInput)] = translated;
                }
            }
            if (failure is null) { SqlStagedSortSource.Release(rows); }
            else { SqlStagedSortSource.ReleaseAfterFailure(rows, failure); }
        }
    }

    [System.Diagnostics.DebuggerDisplay("SQL hash staged input")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(HashGroupInput.DebugView))]
    private sealed class HashGroupInput : DatabaseResultSet
    {
        private readonly SqlStagedSortSource _source;
        internal HashGroupInput(IReadOnlyList<DatabaseColumn> columns, SqlStagedSortSource source)
        { Columns = columns; _source = source; }
        public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
        public override long AffectedCount => -1;
        public override IReadOnlyList<Diagnostic> Diagnostics => [];
        public override IReadOnlyList<DatabaseColumn> Columns { get; }
        public override ValueTask DisposeAsync() => default;
        internal void Consumed() => _source.Transfer();
        public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _source.Read(cancellationToken)) { yield return new SqlMaterializedRow(row); }
            await Task.CompletedTask.ConfigureAwait(false);
        }
        private sealed class DebugView { public DebugView(HashGroupInput target) { } }
    }

    private HashSpill? ActiveHashSpill => HashSpill.For(this);

    // No spill bookkeeping on fitting statements. A staged input also consults eligibility
    // before choosing the input-order collector, to preserve volatile evaluation boundaries.
    private bool CanSpillHashGroup(SqlGroupPlan plan, SqlDatabaseSession? owner)
    {
        if (owner?.Database.Engine.TempSpace is null || !CanPipelineSelectRetention() ||
            plan is SqlGroupingSetsPlan || plan.IsDistinct || plan.Keys.Count == 0 ||
            !IsPlainRetentionInput(plan.Input) || !CanStreamRetentionTree(plan.Input) ||
            HasRetentionSubquery(plan.Input) || HasRetentionSubquery(plan.Having) ||
            plan.Projections.Any(p => HasRetentionSubquery(p.Value)) ||
            plan.OrderBy.Any(o => !o.Collation.IsFixed || HasRetentionSubquery(o.Key)) ||
            HasSequenceProjection(plan.Projections) && plan.OrderBy.Count > 0) { return false; }
        foreach (var key in plan.Keys)
        { if (!key.Collation.IsFixed || HasRetentionSubquery(key.Value)) { return false; } }
        // Every deferred transition must be incapable of a value-dependent error. The root
        // partition files coexist until input ends: even one byte per row is a conservative
        // upper bound on a spilled group's cardinality. The quota is physical, including CRC
        // pages and record framing. COUNT's cardinality cannot overflow Int64 either.
        long rows = owner.Database.Engine.StatementTempSpaceQuota;
        decimal maximum = decimal.MaxValue / Math.Max(1, rows);
        foreach (var aggregate in plan.Aggregates)
        {
            if (aggregate.Function is not (SqlCountFunction or SqlSumFunction or SqlExtremumFunction) ||
                aggregate.Call.Quantifier == Language.SqlSetQuantifier.Distinct || aggregate.OrderBy.Count != 0 ||
                !aggregate.Collation.IsFixed || HasRetentionSubquery(aggregate.Filter) ||
                aggregate.Arguments.Any(HasRetentionSubquery)) { return false; }
            if (aggregate.Function is SqlSumFunction &&
                (!TryHashNumericBound(aggregate.Arguments[0], plan, out decimal bound) || bound > maximum)) { return false; }
            if (aggregate.Function is SqlExtremumFunction && !HasFixedHashArgumentFamily(aggregate.Arguments[0])) { return false; }
        }
        return true;

        static bool HasFixedHashArgumentFamily(SqlBoundExpression argument)
            => argument is SqlBoundColumn or SqlBoundCast or SqlBoundConstant or SqlBoundSequence ||
                argument is SqlBoundCall call && call.Function.ReturnType.Storage.Type != DatabaseType.Null;
    }

    private static bool TryHashNumericBound(SqlBoundExpression expression, SqlGroupPlan plan, out decimal bound)
    {
        if (expression is SqlBoundColumn column)
        {
            var type = plan.SourceColumns[column.Ordinal].Type;
            return Bound(type.Type, type.Precision, type.Scale, out bound);
        }
        if (expression is SqlBoundCast cast)
        { return Bound(cast.Target.Storage.Type, cast.Target.Storage.Precision, cast.Target.Storage.Scale, out bound); }
        if (expression is SqlBoundCall call)
        { return Bound(call.Function.ReturnType.Storage.Type, null, null, out bound); }
        if (expression is SqlBoundSequence) { bound = 9223372036854775808m; return true; }
        if (expression is SqlBoundConstant constant)
        {
            try
            {
                if (constant.Value is null or sbyte or short or int or long or decimal)
                { bound = Math.Abs(Convert.ToDecimal(constant.Value, CultureInfo.InvariantCulture)); return true; }
            }
            catch (OverflowException) { } // Decimal.MinValue's magnitude cannot prove a safe sum.
        }
        bound = 0; return false;

        static bool Bound(DatabaseType type, int? precision, int? scale, out decimal result)
        {
            result = type switch
            {
                DatabaseType.Int8 => 128m, DatabaseType.Int16 => 32768m,
                DatabaseType.Int32 => 2147483648m, DatabaseType.Int64 => 9223372036854775808m,
                DatabaseType.Null => 0m, _ => -1m,
            };
            if (type == DatabaseType.Decimal && precision is > 0 and <= 28 && scale is >= 0 && scale <= precision)
            {
                result = 1m;
                for (int i = 0; i < precision - scale; i++) { result *= 10m; }
            }
            return result >= 0;
        }
    }

    private async ValueTask AppendHashGroupInputAsync(HashSpill spill, object?[] key, object?[] row,
        long ordinal, SqlGroupPlan plan, SqlExpressionEvaluator evaluator, SqlStatementContext statement, CancellationToken token)
    {
        var values = new object?[plan.Aggregates.Sum(a => 1 + a.Arguments.Length)];
        int position = 0;
        foreach (var aggregate in plan.Aggregates)
        {
            await PrepareSubqueriesAsync(aggregate.Filter, row, statement, token).ConfigureAwait(false);
            bool accepted = aggregate.Filter is null || evaluator.Matches(aggregate.Filter, row);
            values[position++] = accepted;
            if (accepted)
            {
                foreach (var argument in aggregate.Arguments)
                { await PrepareSubqueriesAsync(argument, row, statement, token).ConfigureAwait(false); }
                var arguments = new SqlValue[aggregate.Arguments.Length];
                EvaluateArguments(aggregate, evaluator, row, arguments);
                foreach (var argument in arguments) { values[position++] = argument.ToObject(); }
            }
            else { position += aggregate.Arguments.Length; }
        }
        spill.Append(key, values, ordinal, token);
    }

    private async Task<DatabaseResult> FinishHashGroupAsync(HashSpill spill, SqlGroupPlan plan,
        SqlExpressionEvaluator evaluator, SqlStatementContext statement, CancellationToken token)
    {
        var summaries = spill.Complete(token);
        // The ordinal merge is finished before a downstream sort starts. Its single run
        // reader and decoding overlap reserve from the SAME statement ledger as that sort.
        long scratch = StorageTempSpace.PageSize + summaries.MaximumRecordLength + 2 * spill.MaximumDecodedBytes;
        _metrics!.ReservePlainSelectMemory(scratch, _definitions.StatementMemoryBudget);
        Exception? failure = null;
        try
        {
            var ordering = plan.OrderBy.Count == 0
                ? new[] { new SqlBoundOrdering(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.Binary)) }
                : plan.OrderBy;
            using var collected = new SqlRowCollector(ordering, _subqueryValues, plan.Limit, plan.Offset, token, _metrics, SelectBufferBudget);
            collected.ShareSpillStatement(spill.Statement);
            long ordinal = 0;
            foreach (var summary in spill.Read(summaries, token))
            {
                using var rowScope = SqlSequenceExecution.Current?.BeginRow();
                long offset = plan.Offset?.Count ?? 0;
                if (collected.IsComplete || plan.OrderBy.Count == 0 && plan.Limit is { } limit &&
                    limit.Count <= long.MaxValue - offset && ordinal >= offset + limit.Count) { break; }
                int start = summary.Length;
                var row = new object?[start + plan.Projections.Count]; summary.CopyTo(row, 0);
                await PrepareSubqueriesAsync(plan.Having, row, statement, token).ConfigureAwait(false);
                if (!evaluator.Matches(plan.Having, row)) { continue; }
                for (int i = 0; i < plan.Projections.Count; i++)
                {
                    await PrepareSubqueriesAsync(plan.Projections[i].Value, row, statement, token).ConfigureAwait(false);
                    row[start + i] = NormalizeGroupValue(evaluator.Evaluate(plan.Projections[i].Value!, row, copySharedBinary: true), plan.Projections[i].Type);
                }
                var keys = new object?[ordering.Count];
                for (int i = 0; i < keys.Length; i++) { keys[i] = plan.OrderBy.Count == 0 ? ordinal++ : evaluator.Evaluate(ordering[i].Key, row); }
                collected.Add(row[start..], keys, spill.Owner);
            }
            var result = FinishCollected(collected, plan.Projections, plan.Limit, plan.Offset, false, evaluator, token);
            try { summaries.Dispose(); }
            catch (Exception exception)
            { if (result is SqlSpilledResultSet undelivered) { undelivered.DisposeAfterFailure(exception); } throw; }
            SqlSortSpillMetrics.Refresh(_metrics, spill.Statement);
            if (result is SqlSpilledResultSet) { spill.Transfer(); }
            return result;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            _metrics.ReleasePlainSelectMemory(scratch);
            var cleanup = DisposeHashTemp(summaries, failure);
            if (failure is null && cleanup is not null) { throw cleanup; }
        }
    }

    private static Exception? DisposeHashTemp(IDisposable? resource, Exception? initiatingFailure)
    {
        try { resource?.Dispose(); return initiatingFailure; }
        catch (AggregateException cleanup)
        {
            var failure = SqlSpilledResultSet.TranslateCleanup(cleanup);
            if (initiatingFailure is null) { return failure; }
            initiatingFailure.Data[nameof(HashSpill)] = failure; return initiatingFailure;
        }
        catch (StorageTempException cleanup)
        {
            var failure = SqlSpilledResultSet.Translate(cleanup);
            if (initiatingFailure is null) { return failure; }
            initiatingFailure.Data[nameof(HashSpill)] = failure; return initiatingFailure;
        }
    }

    [System.Diagnostics.DebuggerDisplay("SQL hash spill")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(HashSpill.DebugView))]
    private sealed class HashSpill : IDisposable
    {
        private const int partitions = 8, maximumDepth = 8;
        private static readonly ConditionalWeakTable<SqlPlanExecutor, HashSpill> _active = new();
        private long _ordinal = int.MaxValue;
        internal Exception? Failure { get; set; }
        internal static HashSpill? For(SqlPlanExecutor executor) => _active.TryGetValue(executor, out var value) ? value : null;
        internal long NextOrdinal() => _ordinal++;
        internal static void Start(SqlPlanExecutor executor, SqlDatabaseSession owner, Collation[] collations,
            SqlGroupPlan plan, SqlStatementContext context, StorageTempStatement? shared = null)
            => _ = new HashSpill(executor, owner, collations, plan, context, shared);
        private readonly SqlPlanExecutor _executor;
        private readonly SqlGroupPlan? _plan;
        private readonly SqlStatementContext? _context;
        private readonly GroupKeyComparer _comparer;
        private readonly Collation[] _aggregateCollations;
        private readonly SqlTempSortKeyCodec.Column[] _keyColumns;
        private readonly byte[] _families;
        private readonly long _budget;
        private readonly int _bound;
        private readonly StorageTempStatement _statement;
        private readonly StorageTempPartitions _partitions;
        private readonly StorageTempRun _outputs;
        private bool _disposed;
        internal SqlDatabaseSession Owner { get; }
        internal StorageTempStatement Statement => _statement;
        internal void Transfer() => _disposed = true;
        internal long MaximumDecodedBytes { get; private set; }
        // Test-only deterministic observers; no process-global collection of rows.
        internal static Action<string, long>? Observe { get; set; }
        internal static Func<int, int>? PartitionHash { get; set; }

        internal HashSpill(SqlPlanExecutor executor, SqlDatabaseSession owner, Collation[] collations,
            SqlGroupPlan? plan = null, SqlStatementContext? context = null, StorageTempStatement? shared = null)
        {
            _executor = executor; Owner = owner; _plan = plan; _context = context;
            _budget = executor._definitions.StatementMemoryBudget;
            // Handoff happens with live groups: validate against the eventual whole grant.
            long grant = Math.Min(int.MaxValue, _budget);
            long bound = (grant - 32768) / 16 - 8192;
            if (grant < 65536 || bound < 16) { throw SqlExecutionException.StatementMemoryBudget(_budget); }
            _bound = (int)Math.Min(16384, bound);
            _comparer = new GroupKeyComparer(collations);
            _keyColumns = collations.Select(c => new SqlTempSortKeyCodec.Column(false, true, c)).ToArray();
            _families = new byte[collations.Length];
            _aggregateCollations = plan?.Aggregates.Select(a => a.Collation.Resolve(executor._subqueryValues)).ToArray() ?? [];
            SqlSpillLifetime.CheckCapacity(owner);
            _statement = shared ?? owner.Database.Engine.TempSpace!.CreateStatement(owner.Database.Engine.StatementTempSpaceQuota);
            try
            {
                _partitions = StorageTempPartitions.Create(_statement, partitions, partitions * 8704L);
                _outputs = _statement.CreateRun();
                SqlHashSpillMetrics.Begin(executor._metrics!);
                _active.Add(executor, this);
            }
            catch (Exception failure) { SqlSpilledResultSet.ReleaseAfterFailure(_statement, failure); throw; }
        }

        internal void Validate(object?[] key)
        {
            if (!SqlTempRowCodec.TryGetLength(key, out int size) || size + 24L > _bound ||
                !SqlTempSortKeyCodec.TryEncode(key, _keyColumns, _families, out _))
            { throw SqlExecutionException.StatementMemoryBudget(_budget); }
        }

        internal void Append(object?[] key, object?[] values, long ordinal, CancellationToken token)
        {
            Validate(key);
            var row = new object?[1 + key.Length + values.Length]; row[0] = ordinal;
            key.CopyTo(row, 1); values.CopyTo(row, 1 + key.Length);
            AppendPartition(_partitions, row, key, 0, token);
            Observe?.Invoke("input", ordinal);
        }

        private void AppendPartition(StorageTempPartitions target, object?[] row, object?[] key, int depth, CancellationToken token)
        {
            int hash = _comparer.GetHashCode(key);
            if (PartitionHash is { } transform) { hash = transform(hash); }
            // Successive levels consume fresh hash bits, like nodeAgg.c:hashagg_spill_tuple.
            target.Append(unchecked((uint)hash) >> (3 * depth), Encode(row), token);
        }

        internal void EmitGroups(Dictionary<object?[], GroupAggregateState[]> groups, CancellationToken token)
        {
            long ordinal = 0;
            foreach (var entry in groups) { Validate(entry.Key); Emit(entry.Key, entry.Value, ordinal++, token); }
        }

        internal void EmitDistinct(IEnumerable<object?[]> rows, CancellationToken token)
        {
            long ordinal = 0;
            foreach (var row in rows) { Validate(row); Emit(row, [], ordinal++, token); }
        }

        private void Emit(object?[] key, GroupAggregateState[] states, long ordinal, CancellationToken token)
        {
            var row = new object?[1 + key.Length + states.Length]; row[0] = ordinal; key.CopyTo(row, 1);
            for (int i = 0; i < states.Length; i++) { row[1 + key.Length + i] = states[i].Accumulator.Finish(token).ToObject(); }
            MaximumDecodedBytes = Math.Max(MaximumDecodedBytes, SqlTempRowCodec.DecodedBytes(row));
            _outputs.Append(Encode(row), token);
        }

        internal StorageTempRun Complete(CancellationToken token)
        {
            _partitions.Seal(token);
            Process(_partitions, 0, token);
            _outputs.Seal(token);
            Observe?.Invoke("partitioned", _outputs.RecordCount);
            long scratch = StorageTempSpace.PageSize + _outputs.MaximumRecordLength;
            long grant = Math.Min(int.MaxValue, _budget - _executor._metrics!.HashMemoryBytes - scratch);
            int bound = (int)((grant - 32768) / 16 - 8192);
            if (grant < 65536 || bound < 16) { throw SqlExecutionException.StatementMemoryBudget(_budget); }
            _executor._metrics.ReservePlainSelectMemory(grant + scratch, _budget);
            StorageTempSorter? sorter = null;
            StorageTempSortReader? sorted = null;
            Exception? failure = null;
            try
            {
                sorter = StorageTempSorter.Create(_statement, grant, bound, 64);
                foreach (var row in Read(_outputs, token))
                {
                    var key = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(key, (long)row[0]!);
                    sorter.Append(key, Encode(row[1..]), token);
                }
                _outputs.Dispose();
                sorted = sorter.Complete(token);
                SqlHashSpillMetrics.Sort(_executor._metrics, _statement, sorter);
                var output = _statement.CreateRun(token);
                while (sorted.MoveNext(token)) { output.Append(sorted.Value.Span, token); }
                output.Seal(token);
                SqlHashSpillMetrics.Refresh(_executor._metrics, _statement);
                Observe?.Invoke("ordered", output.RecordCount);
                return output;
            }
            catch (Exception exception) { failure = exception; throw; }
            finally
            {
                _executor._metrics.ReleasePlainSelectMemory(grant + scratch);
                var cleanup = DisposeHashTemp(sorted, failure);
                cleanup = DisposeHashTemp(sorter, cleanup);
                if (failure is null && cleanup is not null) { throw cleanup; }
            }
        }

        private void Process(StorageTempPartitions files, int depth, CancellationToken token)
        {
            for (int partition = 0; partition < partitions; partition++)
            {
                if (files.GetPartition(partition) is not { } run) { continue; }
                SqlHashSpillMetrics.Batch(_executor._metrics!, depth + 1);
                Observe?.Invoke("batch", depth + 1);
                var groups = new Dictionary<object?[], HashSpillEntry>(_comparer);
                SqlSelectMemory memory = default;
                long statesBytes = 0;
                SqlSelectMemory[]? aggregateMemory = null;
                StorageTempPartitions? next = null;
                Exception? failure = null;
                try
                {
                    foreach (var row in Read(run, token))
                    {
                        var key = row[1..(1 + _families.Length)];
                        if (!groups.TryGetValue(key, out var entry))
                        {
                            long bytes = memory.RowBytes(key, out bool wide) + (_plan is null ? 64 : PlainGroupStateBytes(_plan.Aggregates) + 32);
                            if (next is null && bytes <= _budget - _executor._metrics!.HashMemoryBytes)
                            {
                                _executor._metrics.ReservePlainSelectMemory(bytes, _budget); statesBytes += bytes;
                                if (wide) { memory.AddRow(key); }
                                var states = _plan is null ? [] : new GroupAggregateState[_plan.Aggregates.Count];
                                for (int i = 0; i < states.Length; i++)
                                {
                                    var aggregate = _plan!.Aggregates[i];
                                    var accumulator = aggregate.Function.CreateAccumulator(new SqlFunctionContext(aggregate.Database, _aggregateCollations[i], token, _context!.Value.TransactionStart));
                                    states[i] = aggregate.Function is SqlExtremumFunction
                                        ? new ExtremumGroupAggregateState(accumulator, aggregate, _executor._subqueryValues)
                                        : new GroupAggregateState(accumulator, aggregate, _executor._subqueryValues);
                                }
                                entry = new HashSpillEntry(states, (long)row[0]!); groups.Add(key, entry);
                            }
                            else
                            {
                                if (groups.Count == 0) { throw SqlExecutionException.StatementMemoryBudget(_budget); }
                                if (depth + 1 >= maximumDepth)
                                { throw new SqlExecutionException(SqlExecutionException.StatementMemoryBudgetCode, string.Format(CultureInfo.CurrentCulture, ErrorMessages.HashSpillDepthExceeded, maximumDepth)); }
                                next ??= StorageTempPartitions.Create(_statement, partitions, partitions * 8704L);
                                AppendPartition(next, row, key, depth + 1, token); continue;
                            }
                        }
                        if (_plan is null) { continue; }
                        int position = 1 + key.Length;
                        for (int i = 0; i < _plan.Aggregates.Count; i++)
                        {
                            var aggregate = _plan.Aggregates[i];
                            bool accepted = (bool)row[position++]!;
                            if (accepted)
                            {
                                var values = row[position..(position + aggregate.Arguments.Length)];
                                _executor.AddPlainAggregateValues(entry.States[i], aggregate, _aggregateCollations[i], values, token, _context!.Value.TransactionStart,
                                    ref statesBytes, ref aggregateMemory, _plan.Aggregates, i);
                            }
                            position += aggregate.Arguments.Length;
                        }
                    }
                    foreach (var entry in groups) { Emit(entry.Key, entry.Value.States, entry.Value.Ordinal, token); }
                    groups.Clear(); groups.TrimExcess(); memory.Clear();
                    _executor.ReleaseGroupMemory(aggregateMemory, ref statesBytes); aggregateMemory = null;
                    run.Dispose(); // Free the parent before the next pass; quota counts live files.
                    if (next is not null) { next.Seal(token); Process(next, depth + 1, token); }
                }
                catch (Exception exception) { failure = exception; throw; }
                finally
                {
                    _executor.ReleaseGroupMemory(aggregateMemory, ref statesBytes);
                    var cleanup = DisposeHashTemp(next, failure);
                    if (failure is null && cleanup is not null) { throw cleanup; }
                }
            }
        }

        private byte[] Encode(object?[] row)
        {
            if (!SqlTempRowCodec.TryGetLength(row, out int size) || size + 24L > _bound || !SqlTempRowCodec.TryEncode(row, out var bytes))
            { throw SqlExecutionException.StatementMemoryBudget(_budget); }
            return bytes;
        }

        internal IEnumerable<object?[]> Read(StorageTempRun run, CancellationToken token)
        {
            using var reader = run.OpenReader(StorageTempSpace.PageSize);
            var buffer = new byte[run.MaximumRecordLength];
            while (reader.TryRead(buffer, out int size, token))
            {
                if (!SqlTempRowCodec.TryDecode(buffer.AsSpan(0, size), out var row))
                { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                yield return row;
            }
        }

        internal DatabaseResult FinishDistinct(IReadOnlyList<SqlProjection> projections, SqlRowCount? limit, SqlRowCount? offset, CancellationToken token)
        {
            var output = Complete(token);
            var columns = ((DatabaseResultSet)MaterializeProjected(projections, [])).Columns;
            var result = new SqlSpilledResultSet(columns, _statement, output, offset?.Count ?? 0, limit?.Count ?? -1, Owner,
                _budget - _executor._metrics!.HashMemoryBytes, MaximumDecodedBytes);
            result.CaptureMetrics(_executor._metrics);
            _disposed = true;
            return result;
        }

        public void Dispose() => Dispose(Failure);
        internal void Dispose(Exception? initiatingFailure)
        {
            _active.Remove(_executor);
            if (_disposed) { return; } _disposed = true;
            try { _statement.Dispose(); }
            catch (AggregateException cleanup)
            {
                var failure = SqlSpilledResultSet.TranslateCleanup(cleanup);
                if (initiatingFailure is null) { throw failure; }
                initiatingFailure.Data[nameof(HashSpill)] = failure;
            }
        }
        private sealed class DebugView { public DebugView(HashSpill target) { } }
    }

    private sealed record HashSpillEntry(GroupAggregateState[] States, long Ordinal);
}
