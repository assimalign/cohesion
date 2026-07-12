using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// The composed pipeline: runs the stage chain and enforces the transaction
/// boundary semantics at the edge — implicit scopes commit on success and roll
/// back on failure, exception, or cancellation; explicit scopes are left to their
/// session. Errors propagate after the rollback, never instead of it.
/// </summary>
internal sealed class BuiltQueryPipeline : IQueryPipeline
{
    private readonly QueryPipelineDelegate _chain;

    internal BuiltQueryPipeline(QueryPipelineDelegate chain)
    {
        _chain = chain;
    }

    /// <inheritdoc />
    public async ValueTask<QueryResult> ExecuteAsync(QueryExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, cancellationToken);
        var token = linked.Token;

        QueryResult result;

        try
        {
            token.ThrowIfCancellationRequested();
            result = await _chain(context, token).ConfigureAwait(false);
        }
        catch
        {
            // Rollback first, then let the original error surface. A rollback
            // failure must not mask the root cause — the scope transitions to
            // Faulted and the first exception wins.
            await TryRollbackImplicitAsync(context).ConfigureAwait(false);
            throw;
        }

        if (context.Transaction.IsImplicit && context.Transaction.Status == QueryTransactionStatus.Active)
        {
            if (result.Status == QueryResultStatus.Success)
            {
                await context.Transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await context.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        return result;
    }

    private static async ValueTask TryRollbackImplicitAsync(QueryExecutionContext context)
    {
        if (!context.Transaction.IsImplicit || context.Transaction.Status != QueryTransactionStatus.Active)
        {
            return;
        }

        try
        {
            // Deliberately not the request token: an aborted request must still
            // release its transaction.
            await context.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original execution error is the actionable one; the scope is
            // Faulted and the engine's recovery path owns the cleanup.
        }
    }
}
