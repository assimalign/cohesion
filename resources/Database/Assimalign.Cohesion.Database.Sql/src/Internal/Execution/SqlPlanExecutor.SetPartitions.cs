using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    [System.Diagnostics.DebuggerDisplay("SQL set-operation stage")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class SetOperationSpill : IDisposable
    {
        private static ConditionalWeakTable<List<object?[]>, SetOperationSpill>? _rows;
        private readonly SqlPlanExecutor _executor;
        private readonly SqlSetOperationPlan _plan;
        private readonly SqlStatementMetrics _metrics;
        private readonly SqlExecutionException _refusal;
        private readonly long _budget;
        private readonly StorageTempStatement _statement;
        private readonly GroupKeyComparer _comparer;
        private readonly byte[] _families;
        private readonly Func<int, int>? _partitionHash;
        private readonly HashSpill? _union;
        private readonly HashSet<object?[]>? _unionSeen;
        private SqlSelectMemory _unionSeenMemory;
        private readonly StorageTempRun? _left;
        private StorageTempRun? _right;
        private StorageTempRun? _output;
        private byte[]? _scratch;
        private long _ordinal;
        private long _maximumDecoded;
        private long _reserved;
        private bool _disposed;
        internal Exception? Failure { get; set; }

        internal SetOperationSpill(SqlPlanExecutor executor, SqlSetOperationPlan plan, SqlStatementContext context,
            SqlExecutionException refusal, bool union, HashSet<object?[]>? unionSeen = null)
        {
            _executor = executor; _plan = plan; _metrics = context.Metrics;
            _budget = executor._definitions.StatementMemoryBudget; _refusal = refusal;
            SqlOperatorSpillRefusal.Record(_metrics, refusal);
            var collations = plan.Projections.Select(projection => projection.Collation.Resolve(executor._subqueryValues)).ToArray();
            _comparer = new GroupKeyComparer(collations); _families = new byte[collations.Length];
            _partitionHash = HashSpillPartitionHash;
            _unionSeen = union && !plan.IsAll ? unionSeen : null;
            try
            {
                _statement = SqlHashSpillMetrics.JoinTemp(_metrics, context.Session!);
                if (union && !plan.IsAll) { _union = new HashSpill(executor, context.Session!, collations, shared: _statement, stage: true); }
                else
                {
                    const long writer = StorageTempSpace.PageSize + 16384 + 768;
                    _metrics.ReservePlainSelectMemory(writer, _budget); _reserved = writer;
                    _left = _statement.CreateRun();
                }
            }
            catch (StorageTempException failure) { ReleaseWorkspace(); Refuse(failure); throw; }
            catch (SqlExecutionException failure) when (!ReferenceEquals(failure, _refusal))
            { ReleaseWorkspace(); Refuse(failure); throw; }
            catch { ReleaseWorkspace(); throw; }
        }

        internal static SetOperationSpill? For(List<object?[]> rows)
            => _rows?.TryGetValue(rows, out var stage) == true ? stage : null;
        internal static void Attach(List<object?[]> rows, SetOperationSpill stage)
        { LazyInitializer.EnsureInitialized(ref _rows).Add(rows, stage); }

        internal void BeginOperand() => ClearUnionSeen();

        internal void Append(object?[] row, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Validate(row);
                SqlRowCollector.ObserveBuffer?.Invoke("set-append", row);
                if (_union is not null)
                {
                    long ordinal = _ordinal++;
                    if (_unionSeen?.Contains(row) == true) { return; }
                    RememberUnionRow(row);
                    _union.Append(row, [], ordinal, token);
                }
                else
                {
                    var record = new object?[1 + row.Length]; record[0] = _ordinal++; row.CopyTo(record, 1);
                    (_right ?? _left)!.Append(Encode(record), token);
                }
            }
            catch (StorageTempException failure) { Refuse(failure); }
            catch (SqlExecutionException failure) when (!ReferenceEquals(failure, _refusal)) { Refuse(failure); }
        }

        // The fitting seen set remains a bounded first filter. Every accepted row
        // already reached HashSpill, so forgetting a full filter cannot lose a key.
        private void RememberUnionRow(object?[] row)
        {
            if (_unionSeen is null) { return; }
            try { _executor.RetainSelectRow(ref _unionSeenMemory, row, includeWindow: true, overhead: 64); }
            catch (SqlExecutionException failure) when (failure.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
            {
                ClearUnionSeen();
                try { _executor.RetainSelectRow(ref _unionSeenMemory, row, includeWindow: true, overhead: 64); }
                catch (SqlExecutionException retry) when (retry.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
                { return; } // One key need not fit this optional filter to fit the later partition pass.
            }
            _unionSeen.Add(row);
        }
        private void ClearUnionSeen()
        {
            if (_unionSeen is null) { return; }
            _unionSeen.Clear(); _unionSeen.TrimExcess(); _executor.ReleaseSelectMemory(ref _unionSeenMemory);
        }

        private void Validate(object?[] row)
        {
            for (int i = 0; i < row.Length; i++)
            {
                if (row[i] is not { } value) { continue; }
                byte family = SqlValueComparer.ComparisonFamily(value);
                if (family == 0 || _families[i] != 0 && _families[i] != family)
                { Refuse(SqlExecutionException.StatementMemoryBudget(_budget)); }
                _families[i] = family;
            }
        }

        private ReadOnlySpan<byte> Encode(object?[] row)
        {
            _scratch ??= new byte[16384];
            if (!SqlTempRowCodec.TryWrite(row, _scratch, out int size))
            {
                if (!SqlTempRowCodec.TryGetLength(row, out _)) { _refusal.Data[nameof(SqlTempRowCodec)] = true; }
                Refuse(SqlExecutionException.StatementMemoryBudget(_budget));
            }
            _maximumDecoded = Math.Max(_maximumDecoded, SqlTempRowCodec.DecodedBytes(row));
            if (StorageTempSpace.PageSize + size + 256L + 2 * _maximumDecoded > _budget)
            { Refuse(SqlExecutionException.StatementMemoryBudget(_budget)); }
            return _scratch.AsSpan(0, size);
        }

        internal void NextSide(CancellationToken token)
        {
            try
            {
                _left!.Seal(token);
                _metrics.ReservePlainSelectMemory(512, _budget); _reserved += 512;
                _right = _statement.CreateRun(token); _ordinal = 0;
            }
            catch (StorageTempException failure) { Refuse(failure); }
            catch (SqlExecutionException failure) when (!ReferenceEquals(failure, _refusal)) { Refuse(failure); }
        }

        internal void Complete(CancellationToken token)
        {
            if (_output is not null) { return; }
            try
            {
                ClearUnionSeen();
                if (_union is not null) { _output = _union.Complete(token); _maximumDecoded = _union.MaximumDecodedBytes; }
                else if (_right is null)
                { _left!.Seal(token); _output = _left; }
                else
                {
                    _right.Seal(token);
                    var survivors = _statement.CreateRun(token);
                    try
                    {
                        Partition(_left!, _right, survivors, 0, token);
                        survivors.Seal(token);
                        _output = Order(survivors, token);
                    }
                    finally { Close(survivors); }
                }
                _scratch = null;
                ReleaseWorkspace();
                SqlSortSpillMetrics.RecordOutput(_metrics, _statement);
                SqlRowCollector.ObserveSpill?.Invoke("set-operation");
            }
            catch (StorageTempException failure) { Refuse(failure); }
            catch (SqlExecutionException failure) when (!ReferenceEquals(failure, _refusal)) { Refuse(failure); }
        }

        // Both complete operands are staged before comparison. A failed partition
        // discards its tentative survivors, then repartitions both entire inputs.
        // No partial partition output reaches the final ordinal merge.
        private void Partition(StorageTempRun left, StorageTempRun right, StorageTempRun output, int depth, CancellationToken token)
        {
            if (depth >= SqlHashSpillPartitioning.MaximumDepth)
            { Refuse(SqlExecutionException.StatementMemoryBudget(_budget)); }
            const long writers = 2 * SqlHashSpillPartitioning.Count * 8704L;
            _metrics.ReservePlainSelectMemory(writers, _budget);
            StorageTempPartitions? leftParts = null, rightParts = null;
            try
            {
                leftParts = StorageTempPartitions.Create(_statement, SqlHashSpillPartitioning.Count, writers / 2);
                rightParts = StorageTempPartitions.Create(_statement, SqlHashSpillPartitioning.Count, writers / 2);
                Distribute(left, leftParts, depth, token);
                Distribute(right, rightParts, depth, token);
                leftParts.Seal(token); rightParts.Seal(token);
            }
            catch
            {
                try { Close(leftParts); Close(rightParts); }
                finally { _metrics.ReleasePlainSelectMemory(writers); }
                throw;
            }
            const long metadata = 2 * SqlHashSpillPartitioning.Count * 512L;
            _metrics.ReleasePlainSelectMemory(writers - metadata);
            try
            {
                // Sealed runs no longer own writer pages. Delete their parents before
                // processing children, sharing the statement's physical quota.
                Close(left); Close(right);
                for (int i = 0; i < SqlHashSpillPartitioning.Count; i++)
                {
                    var l = leftParts!.GetPartition(i); var r = rightParts!.GetPartition(i);
                    if (l is null) { continue; }
                    SqlHashSpillMetrics.Batch(_metrics, depth + 1);
                    if (!TryPartition(l, r, output, token))
                    {
                        if (r is null)
                        {
                            var empty = _statement.CreateRun(token);
                            try { empty.Seal(token); Partition(l, empty, output, depth + 1, token); }
                            finally { Close(empty); }
                        }
                        else { Partition(l, r, output, depth + 1, token); }
                    }
                }
            }
            finally
            {
                try { Close(leftParts); Close(rightParts); }
                finally { _metrics.ReleasePlainSelectMemory(metadata); }
            }
        }

        private void Distribute(StorageTempRun source, StorageTempPartitions target, int depth, CancellationToken token)
        {
            foreach (var record in ReadRun(source, token))
            {
                var key = record[1..];
                int hash = _comparer.GetHashCode(key);
                if (_partitionHash is not null) { hash = _partitionHash(hash); }
                target.Append(SqlHashSpillPartitioning.HashAt(hash, depth), Encode(record), token);
            }
        }

        private bool TryPartition(StorageTempRun left, StorageTempRun? right, StorageTempRun output, CancellationToken token)
        {
            SqlSelectMemory countsBytes = default, seenBytes = default;
            var counts = new Dictionary<object?[], int>(_comparer);
            var seen = _plan.IsAll ? null : new HashSet<object?[]>(_comparer);
            var selected = _statement.CreateRun(token);
            try
            {
                try
                {
                    if (right is not null)
                    {
                        foreach (var record in ReadRun(right, token))
                        {
                            var key = record[1..];
                            if (!counts.TryGetValue(key, out int count)) { _executor.RetainSelectRow(ref countsBytes, key, overhead: 64); }
                            counts[key] = count + 1;
                        }
                    }
                    foreach (var record in ReadRun(left, token))
                    {
                        var key = record[1..];
                        counts.TryGetValue(key, out int count);
                        if (_plan.IsAll && count > 0) { counts[key] = count - 1; }
                        bool include = _plan.Operator == Language.SqlSetOperator.Intersect ? count > 0 : count == 0;
                        if (!include || seen is not null && seen.Contains(key)) { continue; }
                        if (seen is not null) { _executor.RetainSelectRow(ref seenBytes, key, overhead: 64); seen.Add(key); }
                        selected.Append(Encode(record), token);
                    }
                }
                catch (SqlExecutionException failure) when (failure.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
                { return false; }
                finally
                {
                    counts.Clear(); seen?.Clear();
                    _executor.ReleaseSelectMemory(ref countsBytes); _executor.ReleaseSelectMemory(ref seenBytes);
                }
                selected.Seal(token);
                foreach (var record in ReadRun(selected, token)) { output.Append(Encode(record), token); }
                return true;
            }
            finally { Close(selected); }
        }

        private StorageTempRun Order(StorageTempRun survivors, CancellationToken token)
        {
            long scratch = StorageTempSpace.PageSize + survivors.MaximumRecordLength + 256L + 2 * _maximumDecoded;
            long grant = Math.Min(int.MaxValue, _budget - _metrics.HashMemoryBytes - scratch);
            int bound = (int)((grant - 32768) / 16 - 8192);
            if (grant < 65536 || bound < 16) { Refuse(SqlExecutionException.StatementMemoryBudget(_budget)); }
            _metrics.ReservePlainSelectMemory(grant, _budget);
            StorageTempSorter? sorter = null;
            StorageTempSortReader? sorted = null;
            try
            {
                sorter = StorageTempSorter.Create(_statement, grant, bound, 64);
                foreach (var record in ReadRun(survivors, token))
                {
                    if (record[0] is not long ordinal) { Refuse(new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged)); }
                    var key = SqlHashSpillPartitioning.OrdinalKey((long)record[0]!);
                    if (!SqlTempRowCodec.TryEncode(record[1..], out var value) || value.Length + 24L > bound)
                    { Refuse(SqlExecutionException.StatementMemoryBudget(_budget)); }
                    sorter.Append(key, value, token);
                }
                sorted = sorter.Complete(token);
                SqlHashSpillMetrics.Sort(_metrics, _statement, sorter);
                var output = _statement.CreateRun(token);
                while (sorted.MoveNext(token)) { output.Append(sorted.Value.Span, token); }
                output.Seal(token); return output;
            }
            finally { Close(sorted); Close(sorter); _metrics.ReleasePlainSelectMemory(grant); }
        }

        private IEnumerable<object?[]> ReadRun(StorageTempRun run, CancellationToken token)
        {
            long bytes = StorageTempSpace.PageSize + run.MaximumRecordLength + 256L + 2 * _maximumDecoded;
            _metrics.ReservePlainSelectMemory(bytes, _budget);
            try
            {
                using var reader = run.OpenReader(StorageTempSpace.PageSize);
                var buffer = new byte[run.MaximumRecordLength];
                while (reader.TryRead(buffer, out int size, token))
                {
                    if (!SqlTempRowCodec.TryDecode(buffer.AsSpan(0, size), out var row))
                    { Refuse(new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged)); }
                    SqlRowCollector.ObserveBuffer?.Invoke("set-decoded", row);
                    yield return row;
                }
            }
            finally { _metrics.ReleasePlainSelectMemory(bytes); }
        }

        internal IEnumerable<object?[]> Read(CancellationToken token)
        {
            Complete(token);
            foreach (var row in ReadRun(_output!, token))
            { yield return _union is null && _right is null ? row[1..] : row; }
        }
        private void Refuse(Exception cause)
        { _refusal.Data[nameof(SetOperationSpill)] = cause; ExceptionDispatchInfo.Throw(_refusal); }
        private void Close(IDisposable? resource)
        {
            try { resource?.Dispose(); }
            catch (Exception cleanup) when (cleanup is StorageTempException or AggregateException)
            { (Failure ?? _refusal).Data["SetStageCleanup"] = cleanup; }
        }
        private void ReleaseWorkspace()
        { _metrics.ReleasePlainSelectMemory(_reserved); _reserved = 0; }
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true; _scratch = null;
            try { Close(_output); _union?.Dispose(Failure ?? _refusal); Close(_left); Close(_right); }
            finally { ClearUnionSeen(); ReleaseWorkspace(); }
        }
        private sealed class DebugView { public DebugView(SetOperationSpill value) { } }
    }
}
