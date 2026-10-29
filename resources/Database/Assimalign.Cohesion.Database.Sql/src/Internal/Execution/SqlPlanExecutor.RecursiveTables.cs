using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Keep the ordinary executor and non-recursive relation cache layouts unchanged.
    private RecursiveExecution? RecursiveState => (_materializedRelations as RecursiveRelationRows)?.Execution;
    private SqlWindowMemory? RecursiveScratch => RecursiveState is null ? null : SqlWindowMemory.For(_metrics);
    internal static Action<long, long, long>? RecursiveProgress { get; set; }

    private async Task<DatabaseResult> ExecuteRecursiveStatementAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var previous = _materializedRelations;
        using var memory = new SqlWindowMemory(statement.Metrics, _definitions.StatementMemoryBudget, statementBudgetOnly: true);
        var readers = RecursiveReaderCounts(plan);
        memory.Reserve(128L + 96L * readers.Count);
        var execution = new RecursiveExecution(memory, readers);
        _materializedRelations = new RecursiveRelationRows(execution, previous?.TransactionStart ?? statement.TransactionStart);
        try { return await ExecuteAsync(plan, statement, cancellationToken).ConfigureAwait(false); }
        finally
        {
            try { await execution.DisposeAsync().ConfigureAwait(false); }
            finally { _materializedRelations = previous; }
        }
    }

    private async Task<DatabaseResult> ExecuteRecursiveQueryAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        using var rows = new RecursiveRows(RecursiveState!.Memory);
        await foreach (var row in ReadRecursiveQueryAsync(plan, _subqueryValues, statement, cancellationToken).ConfigureAwait(false))
        { rows.Add(row); }
        while (true)
        {
            if (plan is SqlWithPlan with) { plan = with.Input; }
            else if (plan is SqlSubqueryPlan subquery) { plan = subquery.Input; }
            else if (plan is SqlCorrelatedSubqueryPlan correlated) { plan = correlated.Input; }
            else { break; }
        }
        return MaterializeProjected(((SqlRelationPlan)plan).OutputProjections, rows.Detach());
    }

    private async IAsyncEnumerable<object?[]> ReadRecursiveRelationAsync(SqlDerivedRelation relation,
        SqlSubqueryValues? frame, SqlStatementContext statement, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = RecursiveState!;
        var state = root.ScopeFor(frame);
        if (relation.IsRecursiveReference)
        {
            var working = state.Working[relation];
            long end = working.WorkingEnd;
            for (long index = working.WorkingStart; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return working.Rows[index];
            }
            yield break;
        }
        if (!relation.IsCommonTable && relation.CatalogView is null)
        {
            // Derived queries can depend on this iteration's working table. They must
            // never enter the statement-wide CTE cache, even when uncorrelated.
            await using var producer = ReadRecursiveQueryAsync(relation.Query, frame, statement, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool next;
                using (statement.Metrics.EnterRelationAnalysis(relation))
                { next = await producer.MoveNextAsync().ConfigureAwait(false); }
                if (!next) { break; }
                yield return producer.Current;
            }
            yield break;
        }
        if (!state.Tables.TryGetValue(relation, out var table))
        {
            state.Memory.Reserve(512); // table, producer, map entries and fixed scan state
            if (relation.CatalogView is not null)
            {
                // Cached view plans reuse their CTE identities. Each view reference
                // nevertheless owns a separate CTE namespace and producer lifetime.
                frame = new SqlSubqueryValues();
                root.AddScope(frame, relation.Query);
            }
            table = new RecursiveTable(this, relation, frame, statement, cancellationToken, state.Memory,
                state.ReaderCounts.GetValueOrDefault(relation, -1));
            state.Tables.Add(relation, table);
            if (relation.Query is SqlRecursiveUnionPlan recursive) { state.Working.Add(recursive.Working, table); }
        }
        // Each enumeration owns a read pointer; only the furthest reader drives the
        // shared producer (PostgreSQL nodeCtescan.c:74-129).
        var reader = table.OpenReader();
        try
        {
            for (long index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index == table.Rows.Count && !await table.MoveNextAsync().ConfigureAwait(false)) { yield break; }
                yield return table.Rows[index];
                if (reader is not null) { reader.Position = index + 1; table.Reclaim(); }
            }
        }
        finally { await table.CloseReaderAsync(reader).ConfigureAwait(false); }
    }

    [System.Diagnostics.DebuggerDisplay("RecursiveExecution")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class RecursiveExecution : IAsyncDisposable
    {
        private readonly Dictionary<SqlSubqueryValues, RecursiveExecution> _scopes = new(ReferenceEqualityComparer.Instance);
        internal RecursiveExecution(SqlWindowMemory memory, Dictionary<SqlDerivedRelation, int> readers)
        { Memory = memory; ReaderCounts = readers; }
        internal SqlWindowMemory Memory { get; }
        internal Dictionary<SqlDerivedRelation, int> ReaderCounts { get; }
        internal Dictionary<SqlDerivedRelation, RecursiveTable> Tables { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<SqlDerivedRelation, RecursiveTable> Working { get; } = new(ReferenceEqualityComparer.Instance);
        internal void AddScope(SqlSubqueryValues frame, SqlPlan plan)
        {
            var readers = RecursiveReaderCounts(plan);
            Memory.Reserve(384L + 96L * readers.Count);
            _scopes.Add(frame, new RecursiveExecution(Memory, readers));
        }
        internal RecursiveExecution ScopeFor(SqlSubqueryValues? frame)
        {
            for (; frame is not null; frame = frame.Parent)
            { if (_scopes.TryGetValue(frame, out var scope)) { return scope; } }
            return this;
        }
        public async ValueTask DisposeAsync()
        {
            // Stop suspended operators before releasing any working/shared rows.
            try
            {
                foreach (var table in Tables.Values.Reverse()) { await table.CloseAsync().ConfigureAwait(false); }
            }
            finally
            {
                foreach (var scope in _scopes.Values) { await scope.DisposeAsync().ConfigureAwait(false); }
                foreach (var table in Tables.Values) { table.Rows.Dispose(); table.Seen?.Dispose(); }
                Tables.Clear(); Working.Clear(); _scopes.Clear();
            }
        }
        private sealed class DebugView { public DebugView(RecursiveExecution value) { _ = value; } }
    }

    private sealed class RecursiveRelationRows : RelationRows
    {
        internal RecursiveRelationRows(RecursiveExecution execution, DateTimeOffset? start) : base(start) { Execution = execution; }
        internal RecursiveExecution Execution { get; }
    }

    [System.Diagnostics.DebuggerDisplay("RecursiveTable")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class RecursiveTable
    {
        private readonly SqlPlanExecutor _executor;
        private readonly SqlDerivedRelation _relation;
        private readonly SqlSubqueryValues? _frame;
        private readonly SqlStatementContext _statement;
        private readonly CancellationToken _cancellationToken;
        private IAsyncEnumerator<object?[]>? _producer;
        private bool _complete;
        private bool _recursing;
        private long _iterations;
        private long _candidates;
        private readonly SqlWindowMemory _memory;
        private readonly HashSet<RecursiveReader>? _readers;
        private int _unopenedReaders;

        internal RecursiveTable(SqlPlanExecutor executor, SqlDerivedRelation relation, SqlSubqueryValues? frame,
            SqlStatementContext statement, CancellationToken cancellationToken, SqlWindowMemory memory, int readers)
        {
            _executor = executor; _relation = relation; _frame = new SqlSubqueryValues(frame); _statement = statement; _cancellationToken = cancellationToken;
            _memory = memory;
            _unopenedReaders = readers;
            if (readers > 0) { _readers = []; }
            Rows = new(memory);
            if (relation.Query is SqlRecursiveUnionPlan { IsAll: false } plan)
            { Seen = new(memory, new GroupKeyComparer(plan.Projections.Select(p => p.Collation.Resolve(frame)).ToArray())); }
        }
        internal RecursiveRows Rows { get; }
        internal RecursiveDistinct? Seen { get; }
        internal long WorkingStart { get; private set; }
        internal long WorkingEnd { get; private set; }

        internal RecursiveReader? OpenReader()
        {
            if (_unopenedReaders == 0)
            {
                throw new SqlExecutionException("CS05090", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.RecursiveReaderCountExceeded, _relation.Name));
            }
            if (_readers is null) { return null; }
            _memory.Reserve(128);
            var reader = new RecursiveReader();
            _readers.Add(reader); _unopenedReaders--;
            return reader;
        }

        internal async ValueTask CloseReaderAsync(RecursiveReader? reader)
        {
            if (reader is null) { return; }
            _readers!.Remove(reader); _memory.Release(128);
            if (_unopenedReaders == 0 && _readers.Count == 0)
            {
                // No later scan can resume this producer. Closing it also closes
                // readers it suspended in other CTEs before releasing its frontier.
                await CloseAsync().ConfigureAwait(false);
                _complete = true;
                Seen?.Dispose();
                Rows.Dispose();
                return;
            }
            Reclaim();
        }

        internal void Reclaim()
        {
            if (_readers is null || _unopenedReaders != 0) { return; }
            long end = _complete || _relation.Query is not SqlRecursiveUnionPlan ? Rows.Count : WorkingStart;
            foreach (var reader in _readers) { end = Math.Min(end, reader.Position); }
            Rows.ReleaseBefore(end);
        }

        internal async ValueTask<bool> MoveNextAsync()
        {
            if (_complete) { return false; }
            using var analysis = _statement.Metrics.EnterRelationAnalysis(_relation);
            var plan = _relation.Query as SqlRecursiveUnionPlan;
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_producer is null)
                {
                    var operand = plan is null ? null : _recursing ? plan.Recursive : plan.Anchor;
                    _producer = _executor.ReadRecursiveQueryAsync(operand?.Query ?? _relation.Query,
                        _frame is null ? null : new SqlSubqueryValues(_frame), _statement, _cancellationToken).GetAsyncEnumerator(_cancellationToken);
                }
                if (await _producer.MoveNextAsync().ConfigureAwait(false))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var row = _producer.Current;
                    if (plan is not null)
                    {
                        _candidates++;
                        var work = _statement.Metrics.Analysis(plan);
                        if (work is not null) { work.Candidates++; }
                        var values = _recursing ? plan.Recursive.Values : plan.Anchor.Values;
                        var evaluator = _executor.ExecutionEvaluator(_frame, _cancellationToken);
                        var converted = new object?[row.Length];
                        for (int index = 0; index < row.Length; index++)
                        {
                            converted[index] = SqlBinaryValue.CopyShared(SqlTemporalArithmetic.Promote(
                                evaluator.Evaluate(values[index], row), plan.Projections[index].Type));
                        }
                        row = converted;
                        if (Seen is not null && !Seen.Add(row)) { continue; }
                        if (work is not null) { work.RecursiveRows++; }
                    }
                    Rows.Add(row);
                    if (plan is not null) { RecursiveProgress?.Invoke(_iterations, _candidates, Rows.Count); }
                    return true;
                }
                await CloseAsync().ConfigureAwait(false);
                if (plan is null || Rows.Count == WorkingEnd)
                { _complete = true; Reclaim(); return false; }
                WorkingStart = WorkingEnd;
                WorkingEnd = Rows.Count;
                Reclaim();
                _recursing = true;
                _iterations++;
                if (_statement.Metrics.Analysis(plan) is { } counter) { counter.RecursiveIterations++; }
                // The old working range has no remaining producer/rescan after
                // CloseAsync. Readers alone can still own that prefix (PostgreSQL
                // src/backend/executor/nodeRecursiveunion.c:127-144).
            }
        }

        internal async ValueTask CloseAsync()
        {
            var producer = _producer; _producer = null;
            if (producer is not null) { await producer.DisposeAsync().ConfigureAwait(false); }
        }
        private sealed class DebugView { public DebugView(RecursiveTable value) { _ = value; } }
    }

    private sealed class RecursiveReader
    {
        internal long Position { get; set; }
    }

    [System.Diagnostics.DebuggerDisplay("RecursiveRows")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class RecursiveRows : IDisposable
    {
        private readonly SqlWindowMemory _memory;
        private List<object?[]> _rows = [];
        private long _start;
        private int _head;
        private long _bytes;
        internal RecursiveRows(SqlWindowMemory memory) { _memory = memory; }
        internal long Count { get; private set; }
        internal object?[] this[long index] => _rows[(int)((_head + index - _start) % _rows.Capacity)];
        internal void Add(object?[] row)
        {
            int retained = checked((int)(Count - _start));
            if (retained == _rows.Capacity)
            {
                int capacity = _rows.Capacity == 0 ? 4 : checked(_rows.Capacity * 2);
                long next = 24L + 8L * capacity;
                _memory.Reserve(next);
                var rows = new List<object?[]>(capacity);
                for (int index = 0; index < retained; index++) { rows.Add(this[_start + index]); }
                _rows = rows; _head = 0;
                _memory.Release(_bytes); _bytes = next;
            }
            _memory.Retain(row);
            int slot = (_head + retained) % _rows.Capacity;
            if (slot == _rows.Count) { _rows.Add(row); }
            else { _rows[slot] = row; }
            Count++;
        }
        internal void ReleaseBefore(long end)
        {
            while (_start < end)
            {
                var row = _rows[_head];
                _rows[_head] = null!;
                _head = (_head + 1) % _rows.Capacity; _start++;
                _memory.ReleaseRow(row);
            }
        }
        internal List<object?[]> Detach()
        {
            if (_start == 0 && _head == 0)
            {
                // Materialized output has never discarded a prefix. Transfer its
                // existing list without allocating another reference buffer.
                var rows = _rows;
                foreach (var row in rows) { _memory.ReleaseRow(row); }
                _memory.Release(_bytes); _bytes = 0; _rows = []; Count = 0;
                return rows;
            }
            long transferBytes = 24L + 8L * (Count - _start);
            _memory.Reserve(transferBytes);
            try
            {
                var rows = new List<object?[]>(checked((int)(Count - _start)));
                for (long index = _start; index < Count; index++) { rows.Add(this[index]); }
                Dispose();
                return rows;
            }
            finally { _memory.Release(transferBytes); }
        }
        public void Dispose()
        {
            ReleaseBefore(Count);
            _memory.Release(_bytes); _bytes = 0; _rows = []; _start = 0; Count = 0; _head = 0;
        }
        private sealed class DebugView { public DebugView(RecursiveRows value) { _ = value; } }
    }

    [System.Diagnostics.DebuggerDisplay("RecursiveDistinct")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class RecursiveDistinct : IDisposable
    {
        private readonly SqlWindowMemory _memory;
        private HashSet<object?[]>? _rows;
        private readonly Func<IEqualityComparer<object?[]>>? _createComparer;
        private int _capacity;
        private long _bytes;
        internal RecursiveDistinct(SqlWindowMemory memory, IEqualityComparer<object?[]> comparer)
        { _memory = memory; _rows = new(comparer); }
        internal RecursiveDistinct(SqlWindowMemory memory, Func<IEqualityComparer<object?[]>> createComparer)
        { _memory = memory; _createComparer = createComparer; }
        internal bool Add(object?[] row)
        {
            _rows ??= new(_createComparer!());
            if (_rows.Contains(row)) { return false; }
            if (_rows.Count == _capacity)
            {
                int capacity = _capacity == 0 ? 4 : checked(_capacity * 2);
                long next = 128L + 64L * capacity; // prime rounding and old/new arrays coexist
                _memory.Reserve(next);
                _rows.EnsureCapacity(capacity);
                _memory.Release(_bytes); _bytes = next; _capacity = capacity;
            }
            _memory.Retain(row); _rows.Add(row); return true;
        }
        public void Dispose()
        {
            if (_rows is null) { return; }
            foreach (var row in _rows) { _memory.ReleaseRow(row); }
            _rows = null; _memory.Release(_bytes); _bytes = 0;
        }
        private sealed class DebugView { public DebugView(RecursiveDistinct value) { _ = value; } }
    }
}
