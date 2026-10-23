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
internal sealed class SqlQueryExecutor
{
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
            new SqlPlanningException("COHSQLE023", $"More than one parameter key binds the name '{name}'."));

    internal SqlCatalogSnapshot CaptureCatalogSnapshot() => _catalog.CaptureSnapshot();

    /// <summary>
    /// Executes a statement inside the statement's transaction context: the MVCC
    /// context (write stamps, visibility snapshot) and the paired storage bracket
    /// the mutations ride.
    /// </summary>
    internal Task<DatabaseResult> ExecuteAsync(SqlQueryRequest request, SqlStatementContext statement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var parameters = NormalizeParameters(request.Parameters);
        _definitions.EnsureDefinitions(cancellationToken);
        var planner = new SqlPlanner(_catalog, parameters, _definitions.Functions, cancellationToken, statement);
        SqlPlan plan;
        try
        {
            plan = planner.Plan(request.Statement.SqlExpression);
        }
        catch (SqlUnsupportedQueryException exception)
        {
            return Task.FromResult<DatabaseResult>(new SqlQueryResult(DatabaseResultStatus.Error, affectedCount: 0,
                [.. exception.Diagnostics]));
        }

        statement.Metrics.IsPlanned = true;
        var executor = new SqlPlanExecutor(_storage, _catalog, _indexManager, _definitions);
        plan = executor.Cost(plan, statement, cancellationToken);
        return executor.ExecuteAsync(plan, statement, cancellationToken);
    }
}
