using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Executes SQL statements: the planner binds the parsed AST against the catalog,
/// and the plan executor runs it against shared storage inside the session's
/// storage transaction (which owns write-ahead logging and durability).
/// </summary>
internal sealed class SqlQueryExecutor
{
    private readonly SqlStorage _storage;
    private readonly ISqlCatalog _catalog;
    private readonly BTreeIndexManager _indexManager;
    private readonly SqlBoundTableCache _definitions;

    internal SqlQueryExecutor(SqlStorage storage, ISqlCatalog catalog, BTreeIndexManager indexManager, SqlBoundTableCache definitions)
    {
        _storage = storage;
        _catalog = catalog;
        _indexManager = indexManager;
        _definitions = definitions;
    }

    internal ISqlCatalogSnapshot CaptureCatalogSnapshot() => SqlCatalog.CaptureSnapshot(_catalog);

    /// <summary>
    /// Executes a statement inside the statement's transaction context: the MVCC
    /// context (write stamps, visibility snapshot) and the paired storage bracket
    /// the mutations ride.
    /// </summary>
    internal Task<QueryResult> ExecuteAsync(QueryRequest request, SqlStatementContext statement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request is not SqlQueryRequest sqlRequest)
        {
            throw new DatabaseException($"Expected SqlQueryRequest but received {request.GetType().Name}.");
        }

        var planner = new SqlPlanner(_catalog, sqlRequest.Parameters);
        SqlPlan plan;
        try
        {
            plan = planner.Plan(sqlRequest.Statement.SqlExpression);
        }
        catch (SqlUnsupportedQueryException exception)
        {
            return Task.FromResult<QueryResult>(new SqlQueryResult(QueryResultStatus.Error, affectedCount: 0,
                [new Diagnostic { Code = "COHDBL001", Message = exception.Message, Severity = DiagnosticSeverity.Error }]));
        }

        var executor = new SqlPlanExecutor(_storage, _catalog, _indexManager, _definitions, sqlRequest.Parameters);
        return executor.ExecuteAsync(plan, statement, cancellationToken);
    }
}
