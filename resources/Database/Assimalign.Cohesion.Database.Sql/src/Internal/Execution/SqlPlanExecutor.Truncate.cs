using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action? TruncateReserved { get; set; }
    internal static Action? TruncatePrepared { get; set; }
    internal static Action? TruncatePublished { get; set; }

    private async Task<DatabaseResult> ExecuteTruncateAsync(SqlTruncatePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var image = _catalog.CaptureSnapshot();
        var targets = new Dictionary<ulong, SqlCatalogTable>();
        foreach (var target in plan.Expression.Tables)
        {
            var reference = target.Table;
            string schema = reference.SchemaName ?? SqlPlanner.DefaultSchema;
            SqlNamespaces.Require(_catalog, schema);
            if (SqlSystemViews.Find(reference) is { } system)
            {
                throw new SqlExecutionException("CS04021",
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ExecutorExecuteRenameAsyncSqlPlanning, system.Schema, system.Name));
            }
            if (image.Views.FirstOrDefault(view => view.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
                view.Name.Equals(reference.TableName, StringComparison.OrdinalIgnoreCase)) is { } view)
            {
                throw new SqlExecutionException("CS04021", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.ViewReadOnly, view.Schema, view.Name));
            }
            if (!image.TryGetTable(schema, reference.TableName, out var table))
            {
                throw new SqlExecutionException(SqlExecutionException.UndefinedTableCode,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TableMissing, schema, reference.TableName));
            }
            // PostgreSQL tablecmds.c:1915 ignores duplicate relations in the written list.
            targets.TryAdd(table.ObjectId, table);
        }
        var named = targets.Values.ToArray();
        bool added;
        do
        {
            added = false;
            foreach (var child in image.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (targets.ContainsKey(child.ObjectId)) { continue; }
                if (!child.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                    targets.Values.Any(parent => string.Equals(parent.Schema, constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(parent.Name, constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase)))) { continue; }
                if (!plan.Expression.Cascade)
                {
                    throw new SqlExecutionException("CS03067", string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.TruncateForeignKeyDependent, child.Schema, child.Name));
                }
                targets.Add(child.ObjectId, child);
                added = true;
            }
        } while (added);

        // Lock/latch order: metadata admission precedes ascending table X locks (old and
        // reserved owners), then coordinator apply gate, tree directory/tree latch/page pins.
        // No apply gate or page/tree latch spans a catalog publication. The catalog lock's
        // own storage bracket is innermost. Reclamation waits for transaction AND statement
        // pins, so cancellation/rollback cannot expose pages while this flow still uses them.
        var previous = targets.Values.OrderBy(table => table.ObjectId).ToArray();
        var ownedGrants = await LockOwnedSequencesAsync(previous, statement, cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < previous.Length; index++)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(previous[index].ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            if (!_catalog.TryGetTable(previous[index].Schema, previous[index].Name, out var current))
            {
                if (!named.Any(table => table.ObjectId == previous[index].ObjectId))
                {
                    throw new SqlExecutionException(SqlExecutionException.SerializationFailureCode, ErrorMessages.TruncateClosureChanged);
                }
                current = ReadCurrentTable(previous[index]);
            }
            EnsureSameIdentity(previous[index], current);
            previous[index] = current;
        }
        VerifyOwnedSequences(previous, ownedGrants, statement);
        await ReplaceTableStorageAsync(previous, named, null, null, copyRows: false, plan.Expression.RestartIdentity,
            SqlObjectIdentity.Table(plan.Expression.Tables[0].Table.SchemaName ?? SqlPlanner.DefaultSchema, plan.Expression.Tables[0].Table.TableName),
            statement, cancellationToken).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }
}
