using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution.Internal;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Executes SQL statements: the planner binds the parsed AST against the catalog,
/// and the plan executor runs it against shared storage inside the session's
/// storage transaction (which owns write-ahead logging and durability).
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlQueryExecutor")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlQueryExecutor.DebugView))]
internal sealed class SqlQueryExecutor
{
    // Deterministic snapshot-versus-publication tests stop before any table binding.
    internal static Action<SqlQueryRequest>? BeforePlan { get; set; }
    internal static Action<SqlQueryRequest, SqlPlan>? AfterPlan { get; set; }
    internal static Action? BeforeCatalogCapture { get; set; }

    private readonly SqlStorage _storage;
    private readonly SqlCatalog _catalog;
    private readonly BTreeIndexManager _indexManager;
    private readonly SqlBoundTableCache _definitions;

    internal SqlQueryExecutor(SqlStorage storage, SqlCatalog catalog, BTreeIndexManager indexManager, SqlBoundTableCache definitions)
    {
        _storage = storage;
        _catalog = catalog;
        _indexManager = indexManager;
        _definitions = definitions;
    }

    internal static IReadOnlyDictionary<string, object?>? NormalizeParameters(IReadOnlyDictionary<string, object?>? parameters)
        => ParameterBindings.Normalize(parameters, name =>
            new SqlPlanningException("CS01023", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.NormalizeParametersSqlPlanning, name)));

    internal SqlCatalogSnapshot CaptureCatalogSnapshot()
    {
        BeforeCatalogCapture?.Invoke();
        return _catalog.CaptureSnapshot();
    }

    /// <summary>
    /// Executes a statement inside the statement's transaction context: the MVCC
    /// context (write stamps, visibility snapshot) and the paired storage bracket
    /// the mutations ride.
    /// </summary>
    internal Task<DatabaseResult> ExecuteAsync(SqlQueryRequest request, SqlStatementContext statement, CancellationToken cancellationToken = default, SqlDatabaseSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var parameters = NormalizeParameters(request.Parameters);
        _definitions.EnsureDefinitions(cancellationToken);
        BeforePlan?.Invoke(request);
        var planner = new SqlPlanner(_catalog, parameters, _definitions.Functions, cancellationToken, statement);
        SqlPlan plan;
        try
        {
            using var jsonPlanning = SqlJsonPathExecution.BeginPlanning(statement.Metrics, _definitions.StatementMemoryBudget, cancellationToken);
            plan = planner.Plan(request.Statement.SqlExpression, statement.Snapshot);
        }
        catch (SqlUnsupportedQueryException exception)
        {
            return Task.FromResult<DatabaseResult>(new SqlQueryResult(DatabaseResultStatus.Error, affectedCount: 0,
                [.. exception.Diagnostics]));
        }

        AfterPlan?.Invoke(request, plan);
        statement.Metrics.IsPlanned = true;
        var executor = new SqlPlanExecutor(_storage, _catalog, _indexManager, _definitions);
        var sequenceUsage = plan.SequenceUsage;
        if (plan is SqlSequenceScopePlan sequenceScope) { plan = sequenceScope.Input; }
        using (SqlJsonPathExecution.BeginPlanning(statement.Metrics, _definitions.StatementMemoryBudget, cancellationToken))
        { plan = executor.Cost(plan, statement, cancellationToken); }
        return sequenceUsage is not null ? ExecuteSequencesAsync(executor, plan, statement, session, cancellationToken, sequenceUsage)
            : executor.ExecuteAsync(plan, statement, cancellationToken);
    }

    internal async Task<DatabaseResult> ExecuteSequencesAsync(SqlPlanExecutor executor, SqlPlan plan, SqlStatementContext statement,
        SqlDatabaseSession? session, CancellationToken cancellationToken, SqlSequenceUsage? usage = null)
    {
        using var sequences = await SqlSequenceExecution.OpenAsync(_catalog, statement, session?.SequenceState ?? new SqlSequenceSessionState(), cancellationToken, usage ?? plan.SequenceUsage).ConfigureAwait(false);
        sequences.Enter();
        return await executor.ExecuteAsync(plan, statement, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlQueryExecutor value)
        {
            _ = value;
        }
    }
}
