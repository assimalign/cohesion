using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Per-statement execution observability: how the statement reached its rows and
/// how many stored records it had to examine. This is the behavioral proof
/// surface for access-path work — a seek must demonstrably examine O(matches)
/// records where the equivalent scan examines O(table) — consumed by tests
/// through the session's last-statement view.
/// </summary>
/// <remarks>
/// It also records the catalog changes a self-committing statement (DDL) committed by itself,
/// which decide how the session reports the statement's failure on an offline storage (#1272).
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlStatementMetrics")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementMetrics.DebugView))]
internal sealed class SqlStatementMetrics
{
    private bool _capturedCatalogSnapshot;

    // One slot for a scalar path or ordered join paths: plain stored queries pay no extra field.
    private object _accessPath = string.Empty;

    /// <summary>The shared catalog image, supplied at BEGIN or captured lazily by binding.</summary>
    internal SqlCatalogSnapshot? CatalogSnapshot { get; set; }

    /// <summary>Catalog images captured by binding; zero when using a BEGIN-time image.</summary>
    internal int CatalogSnapshotCaptures => _capturedCatalogSnapshot ? 1 : 0;

    internal void CaptureCatalogSnapshot(SqlCatalog catalog)
    {
        if (CatalogSnapshot is null)
        {
            CatalogSnapshot = catalog.CaptureSnapshot();
            _capturedCatalogSnapshot = true;
        }
    }

    /// <summary>Whether the planner produced a plan, including one whose execution later failed.</summary>
    internal bool IsPlanned { get; set; }

    /// <summary>
    /// Gets or sets the number of stored records the statement examined: units
    /// decoded by a scan, or entries fetched by an index seek.
    /// </summary>
    internal long RecordsExamined { get; set; }

    /// <summary>Peak ordered candidate rows retained by any SELECT collector in this statement.</summary>
    internal int PeakOrderedRows { get; set; }

    /// <summary>Candidate rows visited while building self-reference key sets.</summary>
    internal long SelfReferenceCandidateVisits { get; set; }

    /// <summary>Self-reference key-set membership probes, including misses.</summary>
    internal long SelfReferenceProbes { get; set; }

    /// <summary>Scalar key comparisons performed by constraint lookups, including hash collisions.</summary>
    internal long ConstraintKeyComparisons { get; set; }

    /// <summary>Catalog tables visited while discovering incoming references.</summary>
    internal long ReferenceCatalogTableVisits { get; set; }

    /// <summary>Incoming reference lists built for distinct tables in this statement.</summary>
    internal int IncomingReferenceBuilds { get; set; }

    internal int ReferenceIntentLockBatches { get; set; }

    internal int ConstraintLookupBuilds { get; set; }
    internal int LiveIndexBuilds { get; set; }

    internal SqlStatementReferences? References { get; set; }

    // Keep wire-only state in the shared statement object, rather than every context copy.
    internal SqlResultPreparation? ResultPreparation { get; set; }

    // Joined execution shares its outer mark/root with WITH, subqueries and cascade retries.
    // The existing join-only state slot keeps ordinary statement instance sizes unchanged.
    internal (SqlPlan Plan, LockStatementMark Mark)? JoinedDmlStatement
    {
        get => (_accessPath as JoinedDmlWork)?.DmlStatement;
        set
        {
            if (_accessPath is not JoinedDmlWork work)
            {
                if (value is null) { return; }
                // Installed before WITH/subqueries or association enumeration do any work.
                work = new();
                _accessPath = work;
            }
            work.DmlStatement = value;
        }
    }

    /// <summary>All row reads during joined DML, including stamp checks and tombstone pre-images.</summary>
    internal long RowReads => (_accessPath as JoinedDmlWork)?.RowReads ?? 0;

    internal void RecordRowRead()
    {
        if (_accessPath is JoinedDmlWork work) { work.RowReads++; }
    }

    /// <summary>Row reads while collecting target/source associations, before validation and apply.</summary>
    internal long DmlAssociationRowReads
    {
        get => (_accessPath as JoinedDmlWork)?.AssociationRowReads ?? 0;
        set { if (_accessPath is JoinedDmlWork work) { work.AssociationRowReads = value; } }
    }

    /// <summary>Peak target identities retained after the pushed target filter.</summary>
    internal int PeakDmlTargetRows => (_accessPath as JoinedDmlWork)?.PeakTargetRows ?? 0;

    internal void RecordDmlTargetRows(int count)
    {
        if (_accessPath is JoinedDmlWork work) { work.PeakTargetRows = Math.Max(work.PeakTargetRows, count); }
    }

    /// <summary>
    /// Gets or sets the access path the executor drove ("scan", or
    /// "seek:&lt;index&gt;", or one "join-scan"/"join-seek:&lt;index&gt;" entry per join step separated by semicolons), empty for
    /// statements with no table access.
    /// </summary>
    internal string AccessPath
    {
        get => _accessPath is string text ? text
            : _accessPath is JoinWork { Paths.Count: 0, ScalarPath: { } scalar } ? scalar
            : JoinAccessPaths is { Count: 1 } paths ? paths[0]
            : string.Join("; ", JoinAccessPaths);
        set
        {
            if (_accessPath is JoinWork work) { work.ScalarPath = value; }
            else { _accessPath = value; }
        }
    }

    /// <summary>Gets the executed join access paths, one entry per step in written order, including empty inputs.</summary>
    internal IReadOnlyList<string> JoinAccessPaths
    {
        get => _accessPath is JoinWork work ? work.Paths : _accessPath as IReadOnlyList<string> ?? Array.Empty<string>();
        set
        {
            if (_accessPath is JoinWork work) { work.Paths = value; work.ScalarPath = null; }
            else { _accessPath = new JoinWork(value); }
        }
    }

    /// <summary>Actual joined candidates tested, including index-probe matches and hash collisions.</summary>
    internal long JoinCandidatePairs => (_accessPath as JoinWork)?.CandidatePairs ?? 0;

    internal void RecordJoinCandidate()
    {
        if (_accessPath is JoinWork work) { work.CandidatePairs++; }
    }

    internal long HashMemoryBytes => (_accessPath as JoinWork)?.MemoryBytes ?? 0;
    internal long PeakHashMemoryBytes => (_accessPath as JoinWork)?.PeakMemoryBytes ?? 0;

    internal bool IsAnalyzing => (_accessPath as JoinWork)?.Operators is not null;

    internal void BeginAnalysis()
    {
        if (_accessPath is not JoinWork work) { work = new([]); _accessPath = work; }
        work.Operators = new();
    }

    internal SqlOperatorWork? Analysis(object operation)
    {
        if ((_accessPath as JoinWork)?.Operators?.Scope.Work is not { } operators) { return null; }
        if (!operators.TryGetValue(operation, out var work)) { work = new(); operators.Add(operation, work); }
        return work;
    }

    internal long? ActualCandidates(object operation)
        => (_accessPath as JoinWork)?.Operators?.Scope.Work is { } operators && operators.TryGetValue(operation, out var work)
            ? work.Candidates : null;

    // Cached view bodies can appear at several relation paths. Each path owns its own
    // plan counters, while repeated correlated invocations at that path accumulate.
    internal IDisposable? EnterRelationAnalysis(SqlDerivedRelation relation)
    {
        if ((_accessPath as JoinWork)?.Operators is not { } state) { return null; }
        var parent = state.Scope;
        if (!parent.Relations.TryGetValue(relation, out var scope))
        { scope = new(); parent.Relations.Add(relation, scope); }
        state.Scope = scope;
        return new AnalysisLease(state, parent);
    }

    internal SqlAnalysisFrame BeginAnalysisFrame()
    {
        var state = ((JoinWork)_accessPath).Operators!;
        return state.Active = new(state.Active, RecordsExamined, JoinCandidatePairs);
    }

    internal void EndAnalysisFrame(SqlAnalysisFrame frame)
    {
        ((JoinWork)_accessPath).Operators!.Active = frame.Parent;
        if (frame.Parent is { } parent)
        {
            // Propagate inclusive work once to the immediate parent. Each node
            // subtracts its direct children's inclusive work from its own total.
            parent.ChildRecords += RecordsExamined - frame.StartRecords;
            parent.ChildPairs += JoinCandidatePairs - frame.StartPairs;
        }
    }

    internal void ReserveHashMemory(long bytes, long budget)
    {
        if (_accessPath is not JoinWork work) { work = new([]); _accessPath = work; }
        if (bytes > budget - work.MemoryBytes)
        {
            throw new SqlExecutionException(SqlExecutionException.StatementMemoryBudgetCode,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StatementMemoryBudgetExceeded, budget));
        }
        work.MemoryBytes += bytes;
        work.PeakMemoryBytes = Math.Max(work.PeakMemoryBytes, work.MemoryBytes);
    }

    internal void ReleaseHashMemory(long bytes)
    {
        if (_accessPath is JoinWork work) { work.MemoryBytes -= bytes; }
    }

    // Join-only telemetry shares the existing path slot. Scalar statements keep
    // exactly their previous instance layout and allocate no new telemetry object.
    [global::System.Diagnostics.DebuggerDisplay("JoinWork")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementMetrics.JoinWork.DebugView))]
    private class JoinWork
    {
        internal JoinWork(IReadOnlyList<string> paths)
        {
            Paths = paths;
        }

        internal IReadOnlyList<string> Paths { get; set; }
        internal string? ScalarPath;
        internal long CandidatePairs;
        internal long MemoryBytes;
        internal long PeakMemoryBytes;
        internal AnalysisState? Operators;

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementMetrics.JoinWork value)
            {
                _ = value;
            }
        }
    }

    // Inherits the base's effective debugger display/proxy. Ordinary joins retain their
    // original instance layout; only joined DML carries the execution scope.
    private sealed class JoinedDmlWork : JoinWork
    {
        internal JoinedDmlWork() : base([]) { }

        internal (SqlPlan Plan, LockStatementMark Mark)? DmlStatement;
        internal long RowReads;
        internal long AssociationRowReads;
        internal int PeakTargetRows;
    }

    [global::System.Diagnostics.DebuggerDisplay("AnalysisState")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementMetrics.AnalysisState.DebugView))]
    private sealed class AnalysisState
    {
        internal AnalysisScope Scope = new();
        internal SqlAnalysisFrame? Active;

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementMetrics.AnalysisState value)
            {
                _ = value;
            }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("AnalysisScope")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(AnalysisScope.DebugView))]
    private sealed class AnalysisScope
    {
        internal readonly Dictionary<object, SqlOperatorWork> Work = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<SqlDerivedRelation, AnalysisScope> Relations = new(ReferenceEqualityComparer.Instance);

        private sealed class DebugView
        {
            public DebugView(AnalysisScope value) { _ = value; }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("AnalysisLease")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(AnalysisLease.DebugView))]
    private sealed class AnalysisLease : IDisposable
    {
        private readonly AnalysisState _state;
        private readonly AnalysisScope _parent;

        internal AnalysisLease(AnalysisState state, AnalysisScope parent)
        { _state = state; _parent = parent; }

        public void Dispose() => _state.Scope = _parent;

        private sealed class DebugView
        {
            public DebugView(AnalysisLease value) { _ = value; }
        }
    }

    /// <summary>
    /// Gets the number of catalog changes the statement committed by itself that a reopened
    /// database shows: a DDL statement's catalog self-commits that publish, alter or remove a
    /// definition, each counted once its commit returned. A durable step nothing reaches before
    /// such a commit (a table's identity reservation, index trees not yet described) is not
    /// counted. Zero for every other statement, and for a DDL statement that met an offline storage
    /// before it committed such a change, which is then reported as refused rather than unconfirmed
    /// (#1272).
    /// </summary>
    internal int SelfCommits { get; private set; }

    /// <summary>
    /// Records one catalog change the statement committed by itself (<see cref="SelfCommits"/>).
    /// </summary>
    internal void RecordSelfCommit() => SelfCommits++;

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementMetrics value)
        {
            _ = value;
        }
    }
}
