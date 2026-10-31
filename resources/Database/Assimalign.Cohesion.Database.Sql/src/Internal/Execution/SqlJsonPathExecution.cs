using System;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A JSON-only scope sharing the statement's retained memory ledger.</summary>
[System.Diagnostics.DebuggerDisplay("SQL/JSON execution")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlJsonPathExecution : IDisposable
{
    private static readonly AsyncLocal<SqlJsonPathExecution?> _current = new();
    private static readonly ConditionalWeakTable<SqlStatementMetrics, SqlJsonPathExecution> _statements = new();
    [ThreadStatic] private static SqlStatementMetrics? _planningMetrics;
    [ThreadStatic] private static long _planningBudget;
    [ThreadStatic] private static CancellationToken _planningToken;
    private SqlJsonPathExecution? _previous;
    private readonly SqlStatementMetrics? _metrics;
    private readonly long _budget;
    private readonly CancellationToken _cancellationToken;
    private long _reserved;
    private long _visits;

    internal SqlJsonPathExecution(SqlStatementMetrics? metrics = null, long budget = 64L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    { _metrics = metrics; _budget = budget; _cancellationToken = cancellationToken; _previous = Current; }

    internal static SqlJsonPathExecution? Current => _current.Value;
    internal static bool IsStatement(SqlStatementMetrics metrics) => Current?._metrics == metrics;
    internal static SqlJsonPathExecution ForStatement(SqlStatementMetrics metrics, long budget, CancellationToken token)
        => _statements.GetValue(metrics, key => new SqlJsonPathExecution(key, budget, token));
    internal static long VisitsFor(SqlStatementMetrics metrics)
        => _statements.TryGetValue(metrics, out var work) ? work._visits : 0;
    internal static SqlJsonPathExecution ForExpression(CancellationToken token)
        => _planningMetrics is { } metrics ? ForStatement(metrics, _planningBudget, _planningToken)
            : Current ?? new SqlJsonPathExecution(cancellationToken: token);
    // Planning is synchronous. A stack-owned scope preserves nested/reentrant
    // planning without an AsyncLocal allocation on ordinary statements.
    internal static PlanningScope BeginPlanning(SqlStatementMetrics metrics, long budget, CancellationToken token)
        => new(metrics, budget, token);
    internal long Visits => _visits;
    internal long Reserved => _reserved;
    internal void Enter() { _previous = Current; _current.Value = this; }
    internal void Visit()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        // Like window recomputation, path work has one statement-wide cap.
        if (_visits >= SqlStatementMetrics.StatementWorkVisitLimit - (_metrics?.WindowFrameWorkVisits ?? 0))
        { throw new SqlExecutionException("CS05094", ErrorMessages.JsonPathBudget); }
        _visits++;
    }
    internal void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > _budget - (_metrics?.HashMemoryBytes ?? _reserved))
        { throw new SqlExecutionException("CS05094", ErrorMessages.JsonPathBudget); }
        _metrics?.ReserveHashMemory(bytes, _budget); _reserved += bytes;
    }
    internal void ReserveConversion(SqlType target, object? value)
    {
        if (value is null) { return; }
        long length = value switch { string text => text.Length, SqlCharacter text => text.Text.Length, byte[] bytes => bytes.LongLength, _ => 64 };
        if (target.Storage.Type == DatabaseType.Character && target.Storage.MaxLength is int padded)
        { length = Math.Max(length, padded); }
        long multiplier = target.Storage.Type is DatabaseType.Json or DatabaseType.JsonBinary ? 8
            : target.Storage.Type == DatabaseType.Binary ? 3 : 2;
        Reserve(128 + multiplier * length);
    }
    internal void ReleaseTo(long reserved)
    { _metrics?.ReleaseHashMemory(_reserved - reserved); _reserved = reserved; }
    public void Dispose()
    {
        ReleaseTo(0);
        if (ReferenceEquals(Current, this)) { _current.Value = _previous; }
    }
    [System.Diagnostics.DebuggerDisplay("SQL/JSON planning scope")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(PlanningScope.DebugView))]
    internal readonly struct PlanningScope : IDisposable
    {
        private readonly SqlStatementMetrics? _previousMetrics;
        private readonly long _previousBudget;
        private readonly CancellationToken _previousToken;
        internal PlanningScope(SqlStatementMetrics metrics, long budget, CancellationToken token)
        {
            _previousMetrics = _planningMetrics; _previousBudget = _planningBudget; _previousToken = _planningToken;
            _planningMetrics = metrics; _planningBudget = budget; _planningToken = token;
        }
        public void Dispose()
        { _planningMetrics = _previousMetrics; _planningBudget = _previousBudget; _planningToken = _previousToken; }
        private sealed class DebugView { public DebugView(PlanningScope value) { _ = value; } }
    }
    private sealed class DebugView { public DebugView(SqlJsonPathExecution value) { _ = value; } }
}
