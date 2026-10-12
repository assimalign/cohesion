using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
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

    // Invoke every binding outside an exception filter. The CLR ignores a filter's
    // exception, so a renamed SelfCommitted member can otherwise silently stop translating.
    public async ValueTask<bool> InvokeBindingsAsync(string statement, CancellationToken cancellationToken = default)
    {
        var request = SqlQueryRequest.FromSql(statement, null, ParserOptions(_session), _session.Database.Name);
        var previousMetrics = _session.LastStatementMetrics;
        _ = ProvisioningSchema(_session);
        _ = IsSelfCommitting(null, request);
        await using var result = (QueryResultSet)await ExecuteStatementAsync(_session, request, cancellationToken, null).ConfigureAwait(false);
        return SelfCommitted(_session, request, previousMetrics);
    }

    // Mirrors SqlDatabaseSession.ExecuteRequestAsync without its tracing: the same checks and the
    // same receipt parameter and async locals, so Debug's hoisted state machine is the same size.
    private async ValueTask<QueryResult> ExecuteRequestAsync(QueryRequest request, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt = null)
    {
        _session.Database.ThrowIfOffline();
        var previousMetrics = _session.LastStatementMetrics;
        try
        {
            if (ProvisioningSchema(_session) is null && _session.Database.SchemaProvisioner.Deferred is not null)
            {
                await _session.Database.SchemaProvisioner.WaitForTablesAsync(request, cancellationToken).ConfigureAwait(false);
                _session.Database.ThrowIfOffline();
            }
            using var ddl = ProvisioningSchema(_session) is null && IsSelfCommitting(null, request)
                ? _session.Database.DdlInterlock.EnterStatement(_session.Database.Name.ToString()) : null;
            return await ExecuteStatementAsync(_session, request, cancellationToken, scriptReceipt).ConfigureAwait(false);
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
    private static extern ValueTask<QueryResult> ExecuteStatementAsync(SqlDatabaseSession session, QueryRequest request, CancellationToken cancellationToken, SqlCatalogScriptPublication? scriptReceipt);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SelfCommitted")]
    private static extern bool SelfCommitted(SqlDatabaseSession session, QueryRequest request, SqlStatementMetrics? previousMetrics);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_provisioningSchema")]
    private static extern ref string? ProvisioningSchema(SqlDatabaseSession session);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "IsSelfCommitting")]
    private static extern bool IsSelfCommitting(SqlDatabaseSession? session, QueryRequest request);
}
