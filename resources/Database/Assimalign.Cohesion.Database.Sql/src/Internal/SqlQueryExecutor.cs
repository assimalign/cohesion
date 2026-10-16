using System;
using System.Threading;
using System.Threading.Tasks;

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

    internal SqlCatalogSnapshot CaptureCatalogSnapshot() => _catalog.CaptureSnapshot();

    /// <summary>
    /// Executes a statement inside the statement's transaction context: the MVCC
    /// context (write stamps, visibility snapshot) and the paired storage bracket
    /// the mutations ride.
    /// </summary>
    internal Task<QueryResult> ExecuteAsync(SqlQueryRequest request, SqlStatementContext statement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _definitions.EnsureDefinitions(cancellationToken);
        var planner = new SqlPlanner(_catalog, request.Parameters, _definitions.Functions, cancellationToken);
        SqlPlan plan;
        try
        {
            plan = planner.Plan(request.Statement.SqlExpression);
        }
        catch (SqlUnsupportedQueryException exception)
        {
            return Task.FromResult<QueryResult>(new SqlQueryResult(QueryResultStatus.Error, affectedCount: 0,
                [new Diagnostic { Code = "COHDBL001", Message = exception.Message, Severity = DiagnosticSeverity.Error }]));
        }

        statement.Metrics.IsPlanned = true;
        var executor = new SqlPlanExecutor(_storage, _catalog, _indexManager, _definitions);
        return executor.ExecuteAsync(plan, statement, cancellationToken);
    }
}
