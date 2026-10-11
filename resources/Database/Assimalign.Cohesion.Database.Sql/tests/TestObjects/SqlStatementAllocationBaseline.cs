using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Test-only reference for the request wrapper without statement tracing. Both arms use the
/// current parser, session and executor; only the tracing catch/finally calls are absent here.
/// Keep the async locals aligned with ExecuteRequestAsync's non-diagnostic work. Static accessors
/// avoid reflection and avoid adding a shipped visibility seam just for the allocation test.
/// </summary>
internal sealed class SqlStatementAllocationBaseline
{
    private readonly SqlDatabaseSession _session;

    public SqlStatementAllocationBaseline(SqlDatabaseSession session)
    {
        _session = session;
    }

    public ValueTask<QueryResult> ExecuteAsync(string statement, CancellationToken cancellationToken = default)
        => ExecuteRequestAsync(SqlQueryRequest.FromSql(statement, null, ParserOptions(_session), _session.Database.Name), cancellationToken);

    private async ValueTask<QueryResult> ExecuteRequestAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        _session.Database.ThrowIfOffline();
        var previousMetrics = _session.LastStatementMetrics;
        try
        {
            return await ExecuteStatementAsync(_session, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (_session.Database.TranslateOffline(exception,
            selfCommitted: SelfCommitted(_session, request, previousMetrics)) is var translated
            && !ReferenceEquals(translated, exception))
        {
            throw translated;
        }
        catch (Exception exception) when (exception is InsufficientExecutionStackException
            or SqlEvaluationException { Code: SqlEvaluationException.StatementTooComplexCode })
        {
            if (exception is InsufficientExecutionStackException)
            {
                throw SqlEvaluationException.StatementTooComplex((InsufficientExecutionStackException)exception);
            }
            throw;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_parserOptions")]
    private static extern ref SqlQueryParserOptions ParserOptions(SqlDatabaseSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteStatementAsync")]
    private static extern ValueTask<QueryResult> ExecuteStatementAsync(SqlDatabaseSession session, QueryRequest request, CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SelfCommitted")]
    private static extern bool SelfCommitted(SqlDatabaseSession session, QueryRequest request, SqlStatementMetrics? previousMetrics);
}
