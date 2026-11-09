using System;
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
    // Reader, iterator and the two byte-array headers; measured by the marginal cursor test.
    private const long hashReaderObjectBytes = 256;
    internal static Action<string, long>? ObserveHashSpill { get => HashSpill.Observe; set => HashSpill.Observe = value; }
    private static readonly AsyncLocal<Func<int, int>?> _hashSpillPartitionHash = new();
    internal static Func<int, int>? HashSpillPartitionHash { get => _hashSpillPartitionHash.Value; set => _hashSpillPartitionHash.Value = value; }

    // Called only by an input collector's failed reservation. The prefix already ran
    // its projections; the enumerator resumes after that row, without repeating predicates.
    private DatabaseResult SpoolHashGroupInput(List<object?[]> rows, object?[] current,
        IEnumerator<object?[]> remaining, IReadOnlyList<SqlProjection> projections,
        SqlExpressionEvaluator evaluator, ref SqlSelectMemory memory, SqlDatabaseSession owner, CancellationToken token)
    {
        var source = new SqlStagedSortSource(owner, _metrics!, _definitions.StatementMemoryBudget,
            _definitions.StatementMemoryBudget - _metrics!.HashMemoryBytes + memory.Bytes);
        try
        {
            foreach (var row in rows) { source.Append(row, token); }
            rows.Clear(); rows.Capacity = 0; ReleaseSelectMemory(ref memory);
            source.ReserveWriter();
            source.Append(current, token);
            while (remaining.MoveNext()) { source.Append(ProjectRow(remaining.Current, projections, evaluator, token), token); }
            source.Seal(token);
            return new HashGroupInput(((DatabaseResultSet)MaterializeProjected(projections, [])).Columns, source);
        }
        catch (Exception failure) { source.DisposeAfterFailure(failure); throw; }
    }

    [System.Diagnostics.DebuggerDisplay("SQL hash staged input")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(HashGroupInput.DebugView))]
    private sealed class HashGroupInput : DatabaseResultSet
    {
        private readonly SqlStagedSortSource _source;
        private readonly IReadOnlyList<SqlProjection>? _projections;
        private readonly SqlExpressionEvaluator? _evaluator;
        private bool _ownsStatement = true;
        internal HashGroupInput(IReadOnlyList<DatabaseColumn> columns, SqlStagedSortSource source,
            IReadOnlyList<SqlProjection>? projections = null, SqlExpressionEvaluator? evaluator = null)
        { Columns = columns; _source = source; _projections = projections; _evaluator = evaluator; }
        internal StorageTempStatement Statement => _source.Statement;
        internal void Relinquish() => _ownsStatement = false;
        internal Exception? Failure { get; set; }
        public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
        public override long AffectedCount => -1;
        public override IReadOnlyList<Diagnostic> Diagnostics => [];
        public override IReadOnlyList<DatabaseColumn> Columns { get; }
        public override ValueTask DisposeAsync()
        {
            if (_ownsStatement) { var failure = DisposeHashTemp(Statement, Failure); if (Failure is null && failure is not null) { throw failure; } }
            return default;
        }
        internal void Consumed() => _source.Transfer();
        public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _source.Read(cancellationToken))
            { yield return new SqlMaterializedRow(_projections is null ? row : ProjectRow(row, _projections, _evaluator!, cancellationToken)); }
            await Task.CompletedTask.ConfigureAwait(false);
        }
        private sealed class DebugView { public DebugView(HashGroupInput target) { } }
    }

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
            bool fixedBoolean = SqlStandardLibrary.Contains(aggregate.Function) && aggregate.Function.Name is "BOOL_AND" or "BOOL_OR" or "EVERY";
            if (!(aggregate.Function is SqlCountFunction or SqlSumFunction or SqlExtremumFunction || fixedBoolean) ||
                aggregate.Call.Quantifier == Language.SqlSetQuantifier.Distinct || aggregate.OrderBy.Count != 0 ||
                !aggregate.Collation.IsFixed || HasRetentionSubquery(aggregate.Filter) ||
                aggregate.Arguments.Any(HasRetentionSubquery)) { return false; }
            if (aggregate.Function is SqlSumFunction &&
                (!TryHashNumericBound(aggregate.Arguments[0], plan, out decimal bound) || bound > maximum)) { return false; }
            if (aggregate.Function is SqlExtremumFunction && !HasFixedHashArgumentFamily(aggregate.Arguments[0], plan)) { return false; }
        }
        return true;

        static bool HasFixedHashArgumentFamily(SqlBoundExpression argument, SqlGroupPlan group)
        {
            var type = argument switch
            {
                SqlBoundColumn column when HasStoredHashColumn(group, column) => group.SourceColumns[column.Ordinal].Type.Type,
                SqlBoundCast cast => cast.Target.Storage.Type,
                SqlBoundCall { Function.ReturnType.IsPseudo: false } call => call.Function.ReturnType.Storage.Type,
                SqlBoundSequence => DatabaseType.Int64,
                SqlBoundConstant constant => SqlValue.FromObject(constant.Value).Type,
                _ => DatabaseType.Null,
            };
            return type is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64
                or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal or DatabaseType.Boolean
                or DatabaseType.Date or DatabaseType.Time or DatabaseType.DateTime or DatabaseType.DateTimeOffset
                or DatabaseType.TimeSpan or DatabaseType.Guid || argument is SqlBoundConstant { Value: null };
        }
    }

    private static bool HasStoredHashColumn(SqlGroupPlan plan, SqlBoundColumn column)
        => plan.Input is SqlSelectPlan || plan.Bindings is not null && plan.Bindings.Any(binding =>
            binding.Source.Table is not null && column.Ordinal >= binding.Offset &&
            column.Ordinal < binding.Offset + binding.Source.Columns.Count);

    private static bool TryHashNumericBound(SqlBoundExpression expression, SqlGroupPlan plan, out decimal bound)
    {
        if (expression is SqlBoundColumn column)
        {
            // Derived metadata is not a runtime value contract: a polymorphic NULL-input
            // function inside COALESCE can even leave text in a BIGINT-typed column.
            if (!HasStoredHashColumn(plan, column)) { bound = 0; return false; }
            var type = plan.SourceColumns[column.Ordinal].Type;
            return Bound(type.Type, type.Precision, type.Scale, out bound);
        }
        if (expression is SqlBoundCast cast)
        { return Bound(cast.Target.Storage.Type, cast.Target.Storage.Precision, cast.Target.Storage.Scale, out bound); }
        if (expression is SqlBoundCall { Function.ReturnType.IsPseudo: false } call)
        { return Bound(call.Function.ReturnType.Storage.Type, null, null, out bound); }
        if (expression is SqlBoundSequence) { bound = 9223372036854775808m; return true; }
        if (expression is SqlBoundConstant constant && constant.Value is null or sbyte or short or int or long or decimal)
        {
            bound = Math.Abs(Convert.ToDecimal(constant.Value, CultureInfo.InvariantCulture)); return true;
        }
        bound = 0; return false;

        static bool Bound(DatabaseType type, int? precision, int? scale, out decimal result)
        {
            result = type switch
            {
                DatabaseType.Int8 => 128m, DatabaseType.Int16 => 32768m,
                DatabaseType.Int32 => 2147483648m, DatabaseType.Int64 => 9223372036854775808m,
                _ => -1m,
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
                foreach (var argument in arguments)
                { values[position++] = aggregate.Function is SqlCountFunction ? (argument.IsNull ? null : true) : argument.ToObject(); }
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
        long scratch = hashReaderObjectBytes + StorageTempSpace.PageSize + summaries.MaximumRecordLength + 2 * spill.MaximumDecodedBytes;
        _metrics!.ReservePlainSelectMemory(scratch, _definitions.StatementMemoryBudget);
        Exception? failure = null;
        try
        {
            if (plan.OrderBy.Count == 0)
            { return await FinishUnorderedHashGroupAsync(spill, summaries, plan, evaluator, statement, token).ConfigureAwait(false); }
            var ordering = plan.OrderBy;
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

    private async Task<DatabaseResult> FinishUnorderedHashGroupAsync(HashSpill spill, StorageTempRun summaries,
        SqlGroupPlan plan, SqlExpressionEvaluator evaluator, SqlStatementContext statement, CancellationToken token)
    {
        StorageTempRun? output = null;
        long visited = 0, maximumDecoded = 0;
        long offset = plan.Offset?.Count ?? 0;
        foreach (var summary in spill.Read(summaries, token))
        {
            using var rowScope = SqlSequenceExecution.Current?.BeginRow();
            if (plan.Limit is { Count: 0 } || plan.Limit is { } limit && visited >= offset && visited - offset >= limit.Count) { break; }
            int start = summary.Length;
            var row = new object?[start + plan.Projections.Count]; summary.CopyTo(row, 0);
            await PrepareSubqueriesAsync(plan.Having, row, statement, token).ConfigureAwait(false);
            if (!evaluator.Matches(plan.Having, row)) { continue; }
            for (int i = 0; i < plan.Projections.Count; i++)
            {
                await PrepareSubqueriesAsync(plan.Projections[i].Value, row, statement, token).ConfigureAwait(false);
                row[start + i] = NormalizeGroupValue(evaluator.Evaluate(plan.Projections[i].Value!, row, copySharedBinary: true), plan.Projections[i].Type);
            }
            visited++;
            var values = row[start..];
            long grant = Math.Min(int.MaxValue, _definitions.StatementMemoryBudget - _metrics!.HashMemoryBytes);
            long bound = (grant - 32768) / 16 - 8192;
            if (grant < 65536 || !SqlTempRowCodec.TryGetLength(values, out int length) || length + 16L > bound)
            { throw SqlExecutionException.StatementMemoryBudget(_definitions.StatementMemoryBudget); }
            long decoded = SqlTempRowCodec.DecodedBytes(values);
            long scratch = StorageTempSpace.PageSize + length + decoded;
            _metrics!.ReservePlainSelectMemory(scratch, _definitions.StatementMemoryBudget);
            try
            {
                if (!SqlTempRowCodec.TryEncode(values, out var bytes)) { throw SqlExecutionException.StatementMemoryBudget(_definitions.StatementMemoryBudget); }
                output ??= spill.Statement.CreateRun(token);
                output.Append(bytes, token); maximumDecoded = Math.Max(maximumDecoded, decoded);
            }
            finally { _metrics.ReleasePlainSelectMemory(scratch); }
        }
        summaries.Dispose();
        if (output is null)
        { SqlHashSpillMetrics.Refresh(_metrics!, spill.Statement); return MaterializeProjected(plan.Projections, []); }
        output.Seal(token);
        SqlRowCollector.ObserveSpill?.Invoke("output-spool");
        SqlSortSpillMetrics.RecordOutput(_metrics!, spill.Statement);
        var result = new SqlSpilledResultSet(((DatabaseResultSet)MaterializeProjected(plan.Projections, [])).Columns,
            spill.Statement, output, offset, plan.Limit?.Count ?? -1, spill.Owner, _definitions.StatementMemoryBudget - _metrics!.HashMemoryBytes, maximumDecoded);
        result.CaptureMetrics(_metrics); SqlHashSpillMetrics.Refresh(_metrics, spill.Statement); spill.Transfer();
        return result;
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
        private const int partitions = SqlHashSpillPartitioning.Count, maximumDepth = SqlHashSpillPartitioning.MaximumDepth;
        private long _ordinal = int.MaxValue;
        internal long NextOrdinal() => _ordinal++;
        private readonly SqlPlanExecutor _executor;
        private readonly SqlGroupPlan? _plan;
        private readonly SqlStatementContext? _context;
        private readonly GroupKeyComparer _comparer;
        private readonly Collation[] _aggregateCollations;
        private readonly Func<int, int>? _partitionHash;
        private readonly byte[] _families;
        private readonly DatabaseType[] _inputTypes;
        private readonly DatabaseType[] _outputTypes;
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
        private static readonly AsyncLocal<Action<string, long>?> _observe = new();
        internal static Action<string, long>? Observe { get => _observe.Value; set => _observe.Value = value; }

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
            _partitionHash = HashSpillPartitionHash;
            _families = new byte[collations.Length];
            _inputTypes = new DatabaseType[plan?.Aggregates.Sum(a => 1 + a.Arguments.Length) ?? 0];
            _outputTypes = new DatabaseType[plan?.Aggregates.Count ?? 0];
            _aggregateCollations = plan?.Aggregates.Select(a => a.Collation.Resolve(executor._subqueryValues)).ToArray() ?? [];
            SqlSpillLifetime.CheckCapacity(owner);
            _statement = shared ?? SqlHashSpillMetrics.For(executor._metrics!)?.JoinStatement ?? owner.Database.Engine.TempSpace!.CreateStatement(owner.Database.Engine.StatementTempSpaceQuota);
            try
            {
                _partitions = StorageTempPartitions.Create(_statement, partitions, partitions * 8704L);
                _outputs = _statement.CreateRun();
                SqlHashSpillMetrics.Begin(executor._metrics!);
            }
            catch (Exception failure) { SqlSpilledResultSet.ReleaseAfterFailure(_statement, failure); throw; }
        }

        internal void Validate(object?[] key)
        {
            if (!SqlTempRowCodec.TryGetLength(key, out int size) || size + 24L > _bound)
            { throw SqlExecutionException.StatementMemoryBudget(_budget); }
            for (int i = 0; i < key.Length; i++)
            {
                if (key[i] is null) { continue; }
                byte family = SqlValueComparer.ComparisonFamily(key[i]!);
                if (family == 0 || _families[i] != 0 && _families[i] != family)
                { throw SqlExecutionException.StatementMemoryBudget(_budget); }
                _families[i] = family;
            }
        }

        internal void Append(object?[] key, object?[] values, long ordinal, CancellationToken token)
        {
            Validate(key);
            RememberTypes(values, _inputTypes);
            var row = new object?[1 + key.Length + values.Length]; row[0] = ordinal;
            key.CopyTo(row, 1); values.CopyTo(row, 1 + key.Length);
            AppendPartition(_partitions, row, key, 0, token);
            Observe?.Invoke("input", ordinal);
        }

        private void AppendPartition(StorageTempPartitions target, object?[] row, object?[] key, int depth, CancellationToken token)
        {
            int hash = _comparer.GetHashCode(key);
            if (_partitionHash is { } transform) { hash = transform(hash); }
            // Successive levels consume fresh hash bits, like nodeAgg.c:hashagg_spill_tuple.
            target.Append(SqlHashSpillPartitioning.HashAt(hash, depth), Encode(row), token);
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
            RememberTypes(row.AsSpan(1 + key.Length), _outputTypes);
            MaximumDecodedBytes = Math.Max(MaximumDecodedBytes, SqlTempRowCodec.DecodedBytes(row));
            _outputs.Append(Encode(row), token);
        }

        internal StorageTempRun Complete(CancellationToken token)
        {
            _partitions.Seal(token);
            Process(_partitions, 0, token);
            _outputs.Seal(token);
            Observe?.Invoke("partitioned", _outputs.RecordCount);
            long scratch = hashReaderObjectBytes + StorageTempSpace.PageSize + _outputs.MaximumRecordLength + 2 * MaximumDecodedBytes;
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
                foreach (var row in ReadRecords(_outputs, token, ordinal: true))
                {
                    if (row[0] is not long ordinal)
                    { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                    var key = SqlHashSpillPartitioning.OrdinalKey(ordinal);
                    var value = Encode(row[1..]);
                    if (value.Length + 24L > bound)
                    { throw new SqlExecutionException(SqlExecutionException.StatementMemoryBudgetCode, ErrorMessages.HashSpillOrdinalRecordTooLarge); }
                    sorter.Append(key, value, token);
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
                    foreach (var row in ReadRecords(run, token, ordinal: true, input: true))
                    {
                        if (row[0] is not long ordinal)
                        { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
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
                                entry = new HashSpillEntry(states, ordinal); groups.Add(key, entry);
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
                            if (row[position++] is not bool accepted)
                            { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                            if (accepted)
                            {
                                var values = row[position..(position + aggregate.Arguments.Length)];
                                _executor.AddPlainAggregateValues(entry.States[i], aggregate, _aggregateCollations[i], values, token, _context!.Value.TransactionStart,
                                    ref statesBytes, ref aggregateMemory, _plan.Aggregates, i);
                            }
                            position += aggregate.Arguments.Length;
                        }
                    }
                    Observe?.Invoke("batch-live", groups.Count);
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

        internal IEnumerable<object?[]> Read(StorageTempRun run, CancellationToken token) => ReadRecords(run, token);

        private IEnumerable<object?[]> ReadRecords(StorageTempRun run, CancellationToken token, bool ordinal = false, bool input = false)
        {
            using var reader = run.OpenReader(StorageTempSpace.PageSize);
            var buffer = new byte[run.MaximumRecordLength];
            while (reader.TryRead(buffer, out int size, token))
            {
                if (!SqlTempRowCodec.TryDecode(buffer.AsSpan(0, size), out var row) || !MatchesRecord(row, ordinal, input))
                { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                yield return row;
            }
        }

        private bool MatchesRecord(object?[] row, bool ordinal, bool input)
        {
            var types = input ? _inputTypes : _outputTypes;
            int start = ordinal ? 1 : 0;
            if (row.Length != start + _families.Length + types.Length || ordinal && row[0] is not long) { return false; }
            for (int i = 0; i < _families.Length; i++)
            {
                if (row[start + i] is { } value && SqlValueComparer.ComparisonFamily(value) != _families[i]) { return false; }
            }
            start += _families.Length;
            for (int i = 0; i < types.Length; i++)
            {
                if (row[start + i] is { } value && !MatchesStateType(value, types[i])) { return false; }
                if (!input && row[start + i] is null && _plan!.Aggregates[i].Function.IsNeverNull) { return false; }
            }
            // Acceptance flags are never nullable, including rejected rows.
            if (input && _plan is not null)
            {
                int position = start;
                foreach (var aggregate in _plan.Aggregates)
                {
                    if (row[position] is not bool) { return false; }
                    position += 1 + aggregate.Arguments.Length;
                }
            }
            return true;
        }

        private static bool MatchesStateType(object value, DatabaseType type) => type switch
        {
            DatabaseType.Int8 => value is sbyte, DatabaseType.Int16 => value is short,
            DatabaseType.Int32 => value is int, DatabaseType.Int64 => value is long,
            DatabaseType.Float32 => value is float, DatabaseType.Float64 => value is double,
            DatabaseType.Decimal => value is decimal, DatabaseType.Boolean => value is bool,
            DatabaseType.Date => value is DateOnly, DatabaseType.Time => value is TimeOnly,
            DatabaseType.DateTime => value is DateTime, DatabaseType.DateTimeOffset => value is DateTimeOffset,
            DatabaseType.TimeSpan => value is TimeSpan, DatabaseType.Guid => value is Guid,
            _ => false,
        };

        private void RememberTypes(ReadOnlySpan<object?> values, DatabaseType[] types)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is not { } value) { continue; }
                var type = SqlValue.FromObject(value).Type;
                if (types[i] != DatabaseType.Null && types[i] != type)
                { throw SqlExecutionException.StatementMemoryBudget(_budget); }
                types[i] = type;
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

        public void Dispose() => Dispose(null);
        internal void Dispose(Exception? initiatingFailure)
        {
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
