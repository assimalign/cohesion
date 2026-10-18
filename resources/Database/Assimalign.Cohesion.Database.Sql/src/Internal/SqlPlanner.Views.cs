using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Catalog.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static readonly ConditionalWeakTable<SqlCatalogView, ParsedView> _parsedViews = new();
    private ViewBinding? ViewDefinitionBinding
    {
        get => _queryScopes?.ViewBinding;
        set => (_queryScopes ??= new()).ViewBinding = value;
    }

    private SqlCreateViewPlan PlanCreateView(SqlCreateViewExpression create)
    {
        string schema = SqlNamespaces.Require(_catalog, create.View.SchemaName ?? DefaultSchema).Name;
        if (_catalog.TryGetTable(schema, create.View.TableName, out _)) { throw ViewError(ErrorMessages.ViewTargetIsTable); }
        if (_catalog.TryGetView(schema, create.View.TableName, out var existing) && !create.OrReplace) { throw ViewError(ErrorMessages.ViewAlreadyExists); }
        string text = SqlExpressionRenderer.Render(create.Query);
        var query = SqlPersistedExpression.LoadQuery(text, "view " + create.View.TableName);
        if (!SqlPersistedExpression.SameQuery(create.Query, query)) { throw ViewError(ErrorMessages.ViewRoundTrip); }
        if (SqlViewQuery.Nodes(query).Any(node => node is SqlParameterExpression)) { throw ViewError(ErrorMessages.ViewParameters); }
        var binding = new ViewBinding();
        var declaration = new SqlPlanner(_catalog, null, _functions.WithViewCalls(binding.Calls), _cancellationToken);
        declaration.ViewDefinitionBinding = binding;
        var child = declaration.PlanOnce(query);
        var output = declaration.BindDerivedOutput(child, create.View.TableName, create.Columns, query).Columns;
        if (output.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.Count) { throw ViewError(ErrorMessages.ViewDuplicateColumns); }
        var expanded = new StringBuilder(text);
        foreach (var edit in binding.Stars.OrderByDescending(pair => pair.Key)) { expanded.Remove(edit.Key, 1).Insert(edit.Key, edit.Value); }
        var frozen = SqlPersistedExpression.LoadQuery(expanded.ToString(), "view " + create.View.TableName);
        string canonical = SqlExpressionRenderer.Render(frozen);
        if (!SqlPersistedExpression.SameQuery(frozen, SqlPersistedExpression.LoadQuery(canonical, "view " + create.View.TableName))) { throw ViewError(ErrorMessages.ViewRoundTrip); }
        // Resolve after expansion too: traversal positions and call identities now describe
        // exactly the canonical persisted tree, including aggregates and nested queries.
        var finalBinding = new ViewBinding();
        var finalPlanner = new SqlPlanner(_catalog, null, _functions.WithViewCalls(finalBinding.Calls), _cancellationToken);
        finalPlanner.ViewDefinitionBinding = finalBinding;
        var finalPlan = finalPlanner.PlanOnce(frozen);
        var calls = SqlViewQuery.Nodes(frozen).OfType<SqlFunctionCallExpression>()
            .Where(call => !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName)).Select(call => finalBinding.Calls[call]).ToArray();
        var view = new SqlCatalogView(schema, existing?.Name ?? create.View.TableName, canonical, output,
            finalBinding.Tables.ToArray(), finalBinding.Dependencies.Concat(calls).ToArray(), calls);
        _parsedViews.Add(view, new ParsedView(frozen));
        return new SqlCreateViewPlan(view, create.OrReplace);
    }

    internal void BindViews()
    {
        foreach (var view in _catalog.Views)
        {
            // Parsing malformed canonical text fails open. A missing native registration
            // leaves the database readable and fails use with the ordinary definition code.
            _parsedViews.GetValue(view, value => new ParsedView(SqlPersistedExpression.LoadQuery(value.Definition, "view " + value.Schema + "." + value.Name)));
            try { BindCatalogView(view); }
            catch (SqlPlanningException exception) when (exception.Diagnostics[0].Code == "COHSQLE009") { }
        }
    }

    private SqlRelationSource BindCatalogView(SqlCatalogView view)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (ViewDefinitionBinding?.Owner is { } owner && !owner.Dependencies.Contains(SqlObjectIdentity.View(view.Schema, view.Name), StringComparer.OrdinalIgnoreCase))
        { throw ViewError(ErrorMessages.ViewRoundTrip); }
        // Independent planner state makes a stored body a closed lexical scope. Its slot
        // numbers belong to its execution frame, never to a caller's CTE/correlation scope.
        var image = CurrentViewImage();
        var plans = image.Plans;
        var functions = image.Functions.Get(_cancellationToken);
        SqlBindingEntry<ViewPlan> entry;
        lock (plans.Sync)
        {
            if (!plans.Entries.TryGetValue(view, out entry!))
            {
                var catalog = _catalog;
                entry = new SqlBindingEntry<ViewPlan>(token =>
                {
                    if (view.Dependencies.Where(key => key.StartsWith("D:", StringComparison.Ordinal)).Any(key =>
                        !functions.Types.Any(type => type.IsDomain && SqlDefinitionBinder.DomainKey(type.Name).Equals(key, StringComparison.OrdinalIgnoreCase))))
                    { throw new SqlPlanningException("COHSQLE009", ErrorMessages.ViewFunctionMissing); }
                    var query = _parsedViews.GetValue(view, value => new ParsedView(SqlPersistedExpression.LoadQuery(value.Definition, "view " + value.Schema + "." + value.Name))).Query;
                    var functionNodes = SqlViewQuery.Nodes(query).OfType<SqlFunctionCallExpression>()
                        .Where(call => !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName)).ToArray();
                    if (functionNodes.Length != view.FunctionCalls.Count || functionNodes.Where((call, index) =>
                        !view.FunctionCalls[index].StartsWith("F:" + SqlObjectIdentity.Quote(call.FunctionName.ToUpperInvariant()) + "(", StringComparison.Ordinal)).Any())
                    { throw ViewError(ErrorMessages.ViewRoundTrip); }
                    var pins = functionNodes.Select((call, index) => (call, identity: view.FunctionCalls[index])).ToDictionary(pair => pair.call, pair => pair.identity);
                    var planner = new SqlPlanner(catalog, null, functions.WithViewCalls(pins), token);
                    planner.ViewImageState = image;
                    planner.ViewDefinitionBinding = new ViewBinding { Owner = view };
                    var child = planner.PlanOnce(query);
                    var columns = planner.BindDerivedOutput(child, view.Name, view.Columns.Select(column => column.Name).ToArray(), query).Columns;
                    if (columns.Count != view.Columns.Count || columns.Where((column, index) => !SameViewOutput(column, view.Columns[index])).Any())
                    { throw new SqlPlanningException("COHSQLE009", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewContractChanged, view.Schema, view.Name)); }
                    return new ViewPlan(child, columns, planner.ViewDefinitionBinding.SystemRelations);
                });
                plans.Entries.Add(view, entry);
            }
        }
        var bound = entry.Get(_cancellationToken);
        // Re-enter the existing bind-time capture seam even when the immutable body plan
        // is cached. The image is always the caller's statement/transaction image.
        foreach (var system in bound.SystemRelations) { ResolveRelationCore(system, null); }
        if (ViewDefinitionBinding is { } binding)
        {
            binding.Dependencies.Add(SqlObjectIdentity.View(view.Schema, view.Name));

        }
        return new SqlRelationSource(new SqlDerivedRelation(view.Name, bound.Query, bound.Columns) { CatalogView = view });
    }

    private void CaptureViewSelect(SqlSelectExpression select, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, IReadOnlyList<int>? outputOrdinals, SqlExpressionEvaluator scope)
    {
        if (ViewDefinitionBinding is not { } binding) { return; }
        foreach (var node in SqlViewQuery.Expressions(select).Where(root => !select.From.SelectMany(item => item.Joins).Any(join => ReferenceEquals(join.Condition, root))).SelectMany(expression => SqlViewQuery.Nodes(expression, queries: false)))
        {
            if (node is SqlCastExpression cast && SqlDefinitionBinder.ResolveType(cast.TargetType, _functions, cast.Location) is { IsDomain: true } domain)
            { binding.Dependencies.Add(SqlDefinitionBinder.DomainKey(domain.Name)); }
            if (node is SqlColumnReferenceExpression reference)
            {
                int ordinal;
                try { ordinal = scope.ResolveColumn(reference); }
                catch (SqlPlanningException) { continue; } // ORDER BY output aliases bind later.
                if (ordinal < 0) { CaptureOuterViewColumn(reference); continue; }
                AddColumn(ordinal);
                if (reference.TableAlias is null && reference.SchemaName is null && bindings is not null)
                {
                    foreach (var candidate in bindings)
                    {
                        for (int i = 0; i < candidate.Source.Columns.Count; i++)
                        { if (candidate.Source.Columns[i].Name.Equals(reference.ColumnName, StringComparison.OrdinalIgnoreCase)) { AddColumn(candidate.Offset + i); } }
                    }
                }
            }
        }
        foreach (var column in select.Columns)
        {
            if (column.Expression is not SqlStarExpression star) { continue; }
            var names = new List<string>();
            foreach (int ordinal in outputOrdinals ?? Enumerable.Range(0, columns.Count).ToArray())
            {
                AddColumn(ordinal);
                var source = bindings?.First(item => ordinal >= item.Offset && ordinal < item.Offset + item.Source.Columns.Count);
                string name = SqlObjectIdentity.Quote(columns[ordinal].Name);
                if (scope.MergedColumn(ordinal) is null)
                {
                    var relation = source?.Reference ?? select.From[0].Table;
                    string qualifier = relation.Alias ?? (relation as SqlTableReference)?.TableName ?? relation.Alias!;
                    name = SqlObjectIdentity.Quote(qualifier) + "." + name;
                    if (relation is SqlTableReference { Alias: null, SchemaName: { } schema }) { name = SqlObjectIdentity.Quote(schema) + "." + name; }
                }
                else if (bindings is not null)
                {
                    foreach (var candidate in bindings)
                    { for (int i = 0; i < candidate.Source.Columns.Count; i++) { if (candidate.Source.Columns[i].Name.Equals(columns[ordinal].Name, StringComparison.OrdinalIgnoreCase)) { AddColumn(candidate.Offset + i); } } }
                }
                names.Add(name);
            }
            binding.Stars[star.Location!.Start] = string.Join(", ", names);
        }

        void AddColumn(int ordinal)
        {
            var source = bindings?.First(item => ordinal >= item.Offset && ordinal < item.Offset + item.Source.Columns.Count);
            var table = source?.Source.Table;
            if (table is null && bindings is null && select.From[0].Table is SqlTableReference named) { _catalog.TryGetTable(named.SchemaName ?? DefaultSchema, named.TableName, out table!); }
            if (table is not null)
            {
                var column = table.Columns[ordinal - (source?.Offset ?? 0)];
                binding.Dependencies.Add(SqlObjectIdentity.Column(table.ObjectId, column.Name));
                if (column.DomainName is { } domain) { binding.Dependencies.Add(SqlDefinitionBinder.DomainKey(domain)); }
            }
        }
    }

    private void RecordViewColumn(SqlRelationSource source, int ordinal)
    {
        if (ViewDefinitionBinding is not { } binding || source.Table is not { } table) { return; }
        var column = table.Columns[ordinal];
        binding.Dependencies.Add(SqlObjectIdentity.Column(table.ObjectId, column.Name));
        if (column.DomainName is { } domain) { binding.Dependencies.Add(SqlDefinitionBinder.DomainKey(domain)); }
    }

    private void CaptureOuterViewColumn(SqlColumnReferenceExpression reference)
    {
        for (var outer = OuterScope; outer is not null; outer = outer.Parent)
        {
            int ordinal;
            try { ordinal = outer.Local.ResolveLocalColumn(reference); }
            catch (SqlPlanningException) { continue; }
            if (outer.Bindings is { } bindings)
            {
                var source = bindings.First(item => ordinal >= item.Offset && ordinal < item.Offset + item.Source.Columns.Count);
                RecordViewColumn(source.Source, ordinal - source.Offset);
                if (reference.TableAlias is null && outer.Local.MergedColumn(ordinal) is not null)
                {
                    foreach (var candidate in bindings)
                    { for (int i = 0; i < candidate.Source.Columns.Count; i++) { if (candidate.Source.Columns[i].Name.Equals(reference.ColumnName, StringComparison.OrdinalIgnoreCase)) { RecordViewColumn(candidate.Source, i); } } }
                }
            }
            else
            {
                var metadata = outer.Local.ColumnMetadata(reference);
                foreach (var table in ViewImageState?.Snapshot.Tables ?? _catalog.Tables)
                { for (int i = 0; i < table.Columns.Count; i++) { if (ReferenceEquals(metadata, table.Columns[i])) { RecordViewColumn(new SqlRelationSource(table), i); } } }
            }
            return;
        }
    }

    private void CaptureViewJoin(SqlExpression expression, IReadOnlyList<SqlRelationBinding> bindings, SqlExpressionEvaluator scope)
    {
        if (ViewDefinitionBinding is not { } binding) { return; }
        foreach (var node in SqlViewQuery.Nodes(expression, queries: false))
        {
            if (node is SqlCastExpression cast && SqlDefinitionBinder.ResolveType(cast.TargetType, _functions, cast.Location) is { IsDomain: true } domain)
            { binding.Dependencies.Add(SqlDefinitionBinder.DomainKey(domain.Name)); }
            if (node is SqlColumnReferenceExpression reference)
            {
                int ordinal = scope.ResolveColumn(reference);
                if (ordinal < 0) { CaptureOuterViewColumn(reference); continue; }
                var source = bindings.First(item => ordinal >= item.Offset && ordinal < item.Offset + item.Source.Columns.Count);
                RecordViewColumn(source.Source, ordinal - source.Offset);
            }
        }
    }

    private void EnsureViewTargetReadOnly(SqlQueryExpression expression)
    {
        var target = expression switch
        {
            SqlInsertExpression insert => insert.Table,
            SqlUpdateExpression update => update.Table,
            SqlDeleteExpression delete => delete.Table,
            SqlCreateTableExpression create => create.Table,
            SqlDropTableExpression drop => drop.Table,
            SqlAlterTableExpression alter => alter.Table,
            SqlCreateIndexExpression create => create.Table,
            SqlDropIndexExpression drop => drop.Table,
            _ => null,
        };
        if (target is not null && _catalog.TryGetView(target.SchemaName ?? DefaultSchema, target.TableName, out var view))
        { throw new SqlPlanningException("COHSQLE021", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewReadOnly, view.Schema, view.Name), expression.Location); }
    }

    private static bool SameViewOutput(SqlCatalogColumn left, SqlCatalogColumn right)
        => left.Type.Type == right.Type.Type && left.Type.MaxLength == right.Type.MaxLength &&
            left.Type.Precision == right.Type.Precision && left.Type.Scale == right.Type.Scale &&
            left.Collation == right.Collation && string.Equals(left.DomainName, right.DomainName, StringComparison.OrdinalIgnoreCase);

    private static SqlPlanningException ViewError(string message) => new("COHSQLE032", message);
    private sealed record ParsedView(SqlQueryBodyExpression Query);
    internal sealed record ViewPlan(SqlPlan Query, IReadOnlyList<SqlCatalogColumn> Columns, IReadOnlyList<SqlTableReference> SystemRelations);
    internal sealed class ViewPlans
    {
        internal object Sync { get; } = new();
        internal Dictionary<SqlCatalogView, SqlBindingEntry<ViewPlan>> Entries { get; } = new(ReferenceEqualityComparer.Instance);
    }
    internal sealed class ViewBinding
    {
        internal SqlCatalogView? Owner { get; init; }
        internal HashSet<ulong> Tables { get; } = new();
        internal HashSet<string> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal SqlViewCallCapture Calls { get; } = new();
        internal Dictionary<int, string> Stars { get; } = new();
        internal List<SqlTableReference> SystemRelations { get; } = new();
    }
}
