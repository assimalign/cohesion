using System;
using System.Collections.Generic;
using System.Globalization;
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
    private static bool JoinedDml(SqlPlan plan)
    {
        while (true)
        {
            switch (plan)
            {
                case SqlWithPlan with: plan = with.Input; break;
                case SqlSubqueryPlan query: plan = query.Input; break;
                case SqlCorrelatedSubqueryPlan query: plan = query.Input; break;
                default: return plan is SqlUpdatePlan { Source: not null } or SqlDeletePlan { Source: not null };
            }
        }
    }

    // This scope surrounds WITH and independent subquery materialization as well as apply.
    // ApplyStatementAsync finishes undo before propagating an error to this catch.
    private async Task<DatabaseResult> ExecuteJoinedStatementAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var mark = locks.MarkStatement(statement.Transaction.Sequence);
        statement.Metrics.JoinedDmlStatement = (plan, mark);
        try
        {
            if (plan is SqlWithPlan or SqlSubqueryPlan or SqlCorrelatedSubqueryPlan)
            {
                await AcquireDmlSourceIntentLocksAsync(plan, statement, cancellationToken).ConfigureAwait(false);
            }
            return await ExecuteCoreAsync(plan, statement, cancellationToken).ConfigureAwait(false);
        }
        catch when (!_storage.IsOffline)
        {
            locks.ReleaseSince(mark);
            throw;
        }
        finally { statement.Metrics.JoinedDmlStatement = null; }
    }

    private async ValueTask AcquireDmlSourceIntentLocksAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, bool cascade = false)
    {
        var tables = new SortedDictionary<ulong, SqlCatalogTable>();
        var visited = new HashSet<SqlPlan>(ReferenceEqualityComparer.Instance);
        Visit(plan);
        var locks = statement.Coordinator.LockManager;
        var owner = statement.Transaction.Sequence;
        var mark = locks.MarkStatement(owner);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqlCatalogTable? blocked = null;
            foreach (var table in tables.Values)
            {
                if (!locks.TryAcquire(owner, LockResource.Object(table.ObjectId), LockMode.IntentShared))
                { blocked = table; break; }
            }
            if (blocked is null)
            {
                foreach (var table in tables.Values) { EnsureCurrentDefinition(table); }
                return;
            }
            var resource = LockResource.Object(blocked.ObjectId);
            if (cascade) { throw new SqlCascadeLockRetry(resource, LockMode.IntentShared); }
            locks.ReleaseSince(mark);
            await locks.AcquireAsync(owner, resource, LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
            locks.ReleaseSince(mark);
        }

        void Visit(SqlPlan input)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (!visited.Add(input)) { return; }
            switch (input)
            {
                case SqlSelectPlan select: tables[select.Table.ObjectId] = select.Table; break;
                case SqlJoinPlan join:
                    foreach (var binding in join.Bindings)
                    {
                        if (binding.Source.Table is { } table) { tables[table.ObjectId] = table; }
                        if (binding.Source.Derived is { } derived) { Visit(derived.Query); }
                    }
                    break;
                case SqlUpdatePlan { Source: { } source }: Visit(source.Associations); break;
                case SqlDeletePlan { Source: { } source }: Visit(source.Associations); break;
                case SqlWithPlan with:
                    foreach (var relation in with.Relations) { Visit(relation.Query); }
                    Visit(with.Input);
                    break;
                case SqlSubqueryPlan query:
                    foreach (var child in query.Queries) { Visit(child.Query); }
                    Visit(query.Input);
                    break;
                case SqlCorrelatedSubqueryPlan query:
                    foreach (var child in query.Queries) { Visit(child.Query); }
                    Visit(query.Input);
                    break;
                case SqlSetOperationPlan set:
                    foreach (var operand in set.Operands) { Visit(operand.Query); }
                    break;
                case SqlGroupPlan group: Visit(group.Input); break;
            }
        }
    }

    private async ValueTask PrepareJoinedUpdateIntentsAsync(SqlUpdatePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var owner = statement.Transaction.Sequence;
        var scope = statement.Metrics.JoinedDmlStatement!.Value;
        while (true)
        {
            try
            {
                await AcquireDmlSourceIntentLocksAsync(scope.Plan, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                return;
            }
            catch (SqlCascadeLockRetry refused)
            {
                // All metadata intents form one preparation unit. A reference-table wait
                // must release source and wrapper grants too, including target read intents.
                locks.ReleaseSince(scope.Mark);
                await locks.AcquireAsync(owner, refused.Resource, refused.Mode, cancellationToken).ConfigureAwait(false);
                locks.ReleaseSince(scope.Mark);
                statement.References.ClearAfterCascadeRetry();
            }
        }
    }

    private sealed record DmlAssociation(PageId PageId, int SlotIndex, TransactionSequence Writer,
        object?[] TargetValues, object?[] Values);

    private async Task<Dictionary<ulong, DmlAssociation>> CollectDmlAssociationsAsync(SqlDmlSource source,
        SqlStatementContext statement, CancellationToken cancellationToken, bool rejectMultiple)
    {
        var plan = source.Associations;
        var target = plan.Bindings[source.TargetBinding].Source.Table!;
        Dictionary<SqlDerivedRelation, List<object?[]>>? derivedRows = null;
        foreach (var binding in plan.Bindings)
        {
            if (binding.Source.Derived is not { } derived) { continue; }
            (derivedRows ??= new(ReferenceEqualityComparer.Instance))[derived] =
                await MaterializeRelationAsync(derived, statement, cancellationToken).ConfigureAwait(false);
        }
        // Source intents were prepared before wrappers and target write intents.
        // All scans/cursors drain before row-lock waits and the physical apply gate.
        if (plan.Bindings.Any(binding => binding.Source.View is not null)) { _ = statement.RequireCatalogSnapshot(); }

        var identities = new Dictionary<object?[], DmlAssociation>(ReferenceEqualityComparer.Instance);
        foreach (var (location, values) in EnumerateRows(target, plan.Inputs[source.TargetBinding].Access, statement, cancellationToken))
        {
            identities.Add(values, new(location.PageId, location.SlotIndex,
                ReadVersionStamps(target, location.PageId, location.SlotIndex).Writer, values, values));
        }
        var result = new Dictionary<ulong, DmlAssociation>();
        var sharedColumns = new bool[plan.Columns.Count];
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken, sharedColumns);
        // The extra slot retains the original target array by reference through join clones,
        // independent of equal user values, self joins, hidden USING inputs or loop direction.
        await foreach (var row in EnumerateJoinRowsAsync(plan, statement, sharedColumns, cancellationToken,
            derivedRows, (source.TargetBinding, identities.Keys.ToArray())).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PrepareSubqueriesAsync(plan.Where, row, statement, cancellationToken).ConfigureAwait(false);
            if (!evaluator.Matches(plan.Where, row)) { continue; }
            var identity = identities[(object?[])row[^1]!];
            ulong key = SqlRecordLocation.Pack(identity.PageId, identity.SlotIndex);
            if (result.ContainsKey(key))
            {
                if (rejectMultiple)
                {
                    throw new SqlExecutionException(SqlExecutionException.DmlMultipleSourceMatchesCode, string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.DmlMultipleSourceMatches, target.Schema, target.Name));
                }
                continue;
            }
            // Decode owns these binary cells; neither SET nor RETURNING may mutate retained
            // source buffers. Their evaluators request detached binary values on delivery.
            result.Add(key, identity with { Values = row });
        }
        return result;
    }

    private void RecheckDmlTargets(SqlCatalogTable table, Dictionary<ulong, DmlAssociation> associations,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        foreach (var row in associations.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLatestVersion(table, row.PageId, row.SlotIndex, statement.Transaction.Sequence);
            if (ReadVersionStamps(table, row.PageId, row.SlotIndex).Writer != row.Writer)
            {
                throw new TransactionAbortedException(string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.DmlTargetVersionChanged, table.Schema, table.Name));
            }
        }
    }

    private static object?[] DmlReturningRow(object?[] association, object?[] target)
    {
        var row = (object?[])association.Clone();
        target.CopyTo(row, 0);
        return row;
    }
}
