using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Catalog.Internal;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<DatabaseResult> ExecuteCreateViewAsync(SqlCreateViewPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var view = plan.View;
        if (_catalog.TryGetView(view.Schema, view.Name, out var existing))
        { EnsureCanChange(existing.Owner, existing.Name, existing.OwningSchema, "CREATE OR REPLACE VIEW", statement); }
        if (statement.ProvisioningSchema is { } schema)
        { view = new SqlCatalogView(view.Schema, view.Name, view.Definition, view.Columns, view.TableDependencies, view.Dependencies, view.FunctionCalls, DatabaseObjectOwner.Schema, schema); }
        await SelfCommitAsync(statement, statement.ScriptReceipt is { } receipt
            ? _catalog.CreateViewAsync(view, plan.OrReplace, receipt, cancellationToken)
            : _catalog.CreateViewAsync(view, plan.OrReplace, cancellationToken)).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private async Task<DatabaseResult> ExecuteDropViewAsync(SqlDropViewPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (_catalog.TryGetTable(plan.Schema, plan.Name, out _)) { throw new SqlPlanningException("COHSQLE033", ErrorMessages.ViewTargetIsTable); }
        if (!_catalog.TryGetView(plan.Schema, plan.Name, out var view))
        {
            if (!plan.IfExists) { throw new SqlPlanningException("COHSQLE033", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewMissing, plan.Schema, plan.Name)); }
            if (statement.ScriptReceipt is { } absence)
            { await SelfCommitAsync(statement, _catalog.DropViewAsync(plan.Schema, plan.Name, plan.Cascade, absence, cancellationToken)).ConfigureAwait(false); }
            return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
        }
        var views = DependentViews(_catalog.CaptureSnapshot(), [view], []);
        foreach (var dependent in views) { EnsureCanChange(dependent.Owner, dependent.Name, dependent.OwningSchema, "DROP VIEW", statement); }
        await SelfCommitAsync(statement, statement.ScriptReceipt is { } receipt
            ? _catalog.DropViewAsync(view.Schema, view.Name, plan.Cascade, receipt, cancellationToken)
            : _catalog.DropViewAsync(view.Schema, view.Name, plan.Cascade, cancellationToken)).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private void EnsureNoViewReads(SqlCatalogTable table, string? column = null, bool rename = false)
    {
        var dependent = _catalog.Views.FirstOrDefault(view => column is null ? view.TableDependencies.Contains(table.ObjectId)
            : view.Dependencies.Contains(SqlObjectIdentity.Column(table.ObjectId, column), StringComparer.OrdinalIgnoreCase));
        if (dependent is not null)
        { throw new SqlPlanningException("COHSQLD002", rename ? string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewDependent, dependent.Schema, dependent.Name)
            : string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewDropDependent, table.Schema + "." + table.Name + (column is null ? "" : "." + column), dependent.Schema, dependent.Name)); }
    }

    private static SqlCatalogView[] DependentViews(SqlCatalogSnapshot snapshot, IEnumerable<SqlCatalogView> roots, IEnumerable<ulong> tables)
    {
        var identities = tables.ToHashSet();
        var removed = roots.Concat(snapshot.Views.Where(view => view.TableDependencies.Any(identities.Contains)))
            .DistinctBy(view => SqlObjectIdentity.View(view.Schema, view.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(view => SqlObjectIdentity.View(view.Schema, view.Name), StringComparer.OrdinalIgnoreCase);
        bool added;
        do
        {
            added = false;
            foreach (var view in snapshot.Views)
            {
                if (view.Dependencies.Any(removed.ContainsKey)) { added |= removed.TryAdd(SqlObjectIdentity.View(view.Schema, view.Name), view); }
            }
        } while (added);
        return removed.Values.OrderBy(view => view.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(view => view.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
