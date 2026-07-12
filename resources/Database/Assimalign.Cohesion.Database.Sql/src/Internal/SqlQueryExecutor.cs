using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Executes SQL commands against the storage layer. Mutations run inside the
/// session's storage transaction, which owns write-ahead logging and durability.
/// </summary>
internal sealed class SqlQueryExecutor : IQueryExecutor
{
    private readonly SqlStorage _storage;

    internal SqlQueryExecutor(SqlStorage storage)
    {
        _storage = storage;
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
    /// Internal execution method that receives the active storage transaction.
    /// </summary>
    internal Task<QueryResult> ExecuteAsync(QueryRequest request, IStorageTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request is not SqlQueryRequest sqlRequest)
        {
            throw new DatabaseException($"Expected SqlQueryRequest but received {request.GetType().Name}.");
        }

        var commandType = sqlRequest.Statement.SqlExpression.CommandType;

        QueryResult result = commandType switch
        {
            SqlQueryCommandType.Insert => ExecuteInsert(sqlRequest, transaction),
            SqlQueryCommandType.Update => ExecuteUpdate(sqlRequest, transaction),
            SqlQueryCommandType.Delete => ExecuteDelete(sqlRequest, transaction),
            SqlQueryCommandType.Select => ExecuteSelect(sqlRequest),
            _ => throw new DatabaseException($"Unsupported SQL command type: {commandType}.")
        };

        return Task.FromResult(result);
    }

    private QueryResult ExecuteInsert(SqlQueryRequest request, IStorageTransaction transaction)
    {
        var parameters = request.Parameters
            ?? throw new DatabaseException("INSERT requires parameters. Expected 'row' as byte[].");

        if (!parameters.TryGetValue("row", out var rowObj) || rowObj is not byte[] rowData)
        {
            throw new DatabaseException("INSERT requires a 'row' parameter of type byte[].");
        }

        // The storage transaction journals the page before-image ahead of the
        // mutation and the after-image at commit (the write-ahead rule).
        _storage.InsertRow(transaction, rowData);

        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 1);
    }

    private QueryResult ExecuteUpdate(SqlQueryRequest request, IStorageTransaction transaction)
    {
        var parameters = request.Parameters
            ?? throw new DatabaseException("UPDATE requires parameters. Expected 'pageId', 'slotIndex', and 'row'.");

        if (!parameters.TryGetValue("pageId", out var pageIdObj) || pageIdObj is not PageId pageId)
        {
            throw new DatabaseException("UPDATE requires a 'pageId' parameter of type PageId.");
        }

        if (!parameters.TryGetValue("slotIndex", out var slotObj) || slotObj is not int slotIndex)
        {
            throw new DatabaseException("UPDATE requires a 'slotIndex' parameter of type int.");
        }

        if (!parameters.TryGetValue("row", out var rowObj) || rowObj is not byte[] rowData)
        {
            throw new DatabaseException("UPDATE requires a 'row' parameter of type byte[].");
        }

        _storage.UpdateRow(transaction, pageId, slotIndex, rowData);

        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 1);
    }

    private QueryResult ExecuteDelete(SqlQueryRequest request, IStorageTransaction transaction)
    {
        var parameters = request.Parameters
            ?? throw new DatabaseException("DELETE requires parameters. Expected 'pageId' and 'slotIndex'.");

        if (!parameters.TryGetValue("pageId", out var pageIdObj) || pageIdObj is not PageId pageId)
        {
            throw new DatabaseException("DELETE requires a 'pageId' parameter of type PageId.");
        }

        if (!parameters.TryGetValue("slotIndex", out var slotObj) || slotObj is not int slotIndex)
        {
            throw new DatabaseException("DELETE requires a 'slotIndex' parameter of type int.");
        }

        _storage.DeleteRow(transaction, pageId, slotIndex);

        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 1);
    }

    private QueryResult ExecuteSelect(SqlQueryRequest request)
    {
        // Full table scan via the unit iterator
        var iterator = _storage.GetUnitIterator();
        return new SqlQueryResultSet(iterator);
    }
}
