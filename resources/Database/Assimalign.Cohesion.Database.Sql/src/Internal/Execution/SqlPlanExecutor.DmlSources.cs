using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Observation runs after all enumerators have been disposed, before row-lock acquisition.
    internal static Action? DmlAssociationsCollected { get; set; }

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
        // Intent locks precede reads; all scans/cursors release their page/tree latches before
        // yielding. Drain every input before waiting for sorted target row locks, then parent
        // and unique-key locks, then the coordinator apply gate and storage/index latches.
        // No asynchronous lock wait holds a storage latch or the apply gate.
        foreach (var table in plan.Bindings.Select(binding => binding.Source.Table).OfType<SqlCatalogTable>()
            .DistinctBy(table => table.ObjectId).OrderBy(table => table.ObjectId))
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence,
                LockResource.Object(table.ObjectId), LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
            EnsureCurrentDefinition(table);
        }
        if (plan.Bindings.Any(binding => binding.Source.View is not null)) { _ = statement.RequireCatalogSnapshot(); }

        var identities = new Dictionary<object?[], DmlAssociation>(ReferenceEqualityComparer.Instance);
        foreach (var version in ScanVersions(target, cancellationToken, statement.Metrics))
        {
            if (!statement.Snapshot.IsVisible(version.Writer) ||
                version.Deleter != TransactionSequence.None && statement.Snapshot.IsVisible(version.Deleter)) { continue; }
            identities.Add(version.Values, new(version.Location.PageId, version.Location.SlotIndex,
                version.Writer, version.Values, version.Values));
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
        DmlAssociationsCollected?.Invoke();
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
