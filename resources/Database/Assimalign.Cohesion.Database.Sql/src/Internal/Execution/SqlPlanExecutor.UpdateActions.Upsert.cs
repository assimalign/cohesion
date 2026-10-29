using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<DatabaseResult> ExecuteReferentialUpsertAsync(SqlCatalogTable table, List<SqlUpsertWrite> writes,
        SqlOnConflictPlan plan, IReadOnlyList<SqlProjection>? returning, SqlStatementContext statement,
        CancellationToken cancellationToken, LockStatementMark initialMark)
    {
        bool applying = false;
        try
        {
            using var attempt = statement.Metrics.BeginReferentialAttempt(_definitions.StatementMemoryBudget);
            await AcquireReferenceIntentLocksAsync(table, statement, cancellationToken, cascade: true).ConfigureAwait(false);
            var rows = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>();
            var direct = new List<SqlConstraintUpdate>(writes.Count);
            int insertion = 0;
            foreach (var write in writes)
            {
                var location = write.OldLocation is { } old ? SqlRecordLocation.Unpack(old) : (new PageId(-1L - insertion++), 0);
                var row = new SqlConstraintUpdate(table, location, write.OldValues ?? [], (object?[])write.Values.Clone(),
                    write.OldLocation.HasValue ? plan.Assignments.Select(assignment => assignment.Ordinal).ToHashSet() : []);
                rows.Add((table.ObjectId, SqlRecordLocation.Pack(location.Item1, location.Item2)), row);
                var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!;
                if (write.OldValues is not null) { memory.Retain(write.OldValues); }
                memory.Retain(write.Values);
                memory.Retain(write.Record);
                memory.Retain(row.NewValues);
                memory.Reserve(384L + 32L * row.Assigned.Count);
                direct.Add(row);
            }
            var pending = await CollectReferentialUpdatesAsync([], [], statement, cancellationToken, rows, validateUpdates: true).ConfigureAwait(false);
            var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
            var returned = returning is null ? null : new List<object?[]>(direct.Count);
            if (returned is not null)
            { foreach (var write in writes) { returned.Add(ProjectRow(write.Values, returning!, evaluator, cancellationToken)); } }
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                applying = true;
                await ApplyConstraintUpdatesAsync(pending, statement, bracket, cancellationToken).ConfigureAwait(false);
                return await statement.PrepareResultAsync(returned is null ? new SqlQueryResult(DatabaseResultStatus.Success, direct.Count)
                    : MaterializeProjected(returning!, returned, direct.Count), cancellationToken).ConfigureAwait(false);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlCascadeLockRetry) { throw; } // arbitration owns release/restart
        catch when (!applying && !_storage.IsOffline)
        {
            statement.Coordinator.LockManager.ReleaseSince(initialMark);
            throw;
        }
    }
}
