using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Executes SQL statements: the planner binds the parsed AST against the catalog,
/// and the plan executor runs it against shared storage inside the session's
/// storage transaction (which owns write-ahead logging and durability).
/// </summary>
internal sealed class SqlQueryExecutor : IQueryExecutor
{
    private readonly SqlStorage _storage;
    private readonly ISqlCatalog _catalog;

    internal SqlQueryExecutor(SqlStorage storage, ISqlCatalog catalog)
    {
        _storage = storage;
        _catalog = catalog;
    }

    /// <summary>
    /// Public interface method — requires a transaction context from the session.
    /// </summary>
    public Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "SQL query execution requires a transaction context. Use IDatabaseSession.ExecuteAsync() instead.");
    }

    /// <summary>
    /// Internal execution method that receives the statement's transaction
    /// context: the MVCC context (write stamps, visibility snapshot) and the
    /// paired storage bracket the mutations ride.
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
        var plan = planner.Plan(sqlRequest.Statement.SqlExpression);

        var executor = new SqlPlanExecutor(_storage, _catalog, sqlRequest.Parameters);
        return executor.ExecuteAsync(plan, statement, cancellationToken);
    }
}
