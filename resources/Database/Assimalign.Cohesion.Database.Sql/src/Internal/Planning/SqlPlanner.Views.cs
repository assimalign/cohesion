using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

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
        var query = SqlPersistedExpression.LoadQuery(text, "view " + create.View.TableName, declaring: true);
        if (!SqlPersistedExpression.SameQuery(create.Query, query)) { throw ViewError(ErrorMessages.ViewRoundTrip); }
        if (SqlViewQuery.Nodes(query).Any(node => node is SqlParameterExpression)) { throw ViewError(ErrorMessages.ViewParameters); }
        var binding = new ViewBinding();
        var declaration = new SqlPlanner(_catalog, null, _functions.WithViewCalls(binding.Calls), _cancellationToken);
        declaration.ViewDefinitionBinding = binding;
        var child = declaration.PlanOnce(query);
        var output = declaration.BindDerivedOutput(child, create.View.TableName, create.Columns, query).Columns;
        if (output.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.Count) { throw ViewError(ErrorMessages.ViewDuplicateColumns); }
        var expanded = new StringBuilder(text);
        foreach (var edit in binding.Edits.OrderByDescending(pair => pair.Key)) { expanded.Remove(edit.Key, edit.Value.Length).Insert(edit.Key, edit.Value.Text); }
        var frozen = SqlPersistedExpression.LoadQuery(expanded.ToString(), "view " + create.View.TableName, declaring: true);
        string canonical = SqlExpressionRenderer.Render(frozen);
        if (!SqlPersistedExpression.SameQuery(frozen, SqlPersistedExpression.LoadQuery(canonical, "view " + create.View.TableName, declaring: true))) { throw ViewError(ErrorMessages.ViewRoundTrip); }
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
            // Malformed canonical text fails open; body binding failures are isolated to use.
            _parsedViews.GetValue(view, value => new ParsedView(SqlPersistedExpression.LoadQuery(value.Definition, "view " + value.Schema + "." + value.Name)));
            try { BindCatalogView(view); }
            catch (Exception exception) when (IsViewBindingFailure(exception)) { }
        }
    }

    private SqlRelationSource BindCatalogView(SqlCatalogView view)
    {
        try { return BindCatalogViewCore(view); }
        // Stored bodies may fail after application registrations change, or expose a binder
        // defect. Isolate every recoverable failure to this view, preserving its original cause.
        catch (Exception exception) when (IsViewBindingFailure(exception))
        {
            string code = exception is DatabaseException { Diagnostics.Count: > 0 } database
                ? database.Diagnostics[0].Code ?? "CS03033" : "CS03033";
            throw new SqlPlanningException(code, string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ViewBindingFailed, view.Schema, view.Name, exception.Message), innerException: exception);
        }
    }

    private static bool IsViewBindingFailure(Exception exception)
        => exception is not (OperationCanceledException or InsufficientExecutionStackException or OutOfMemoryException or StorageException or IOException)
            && (exception.InnerException is null || IsViewBindingFailure(exception.InnerException))
            && (exception is not AggregateException aggregate || aggregate.InnerExceptions.All(IsViewBindingFailure));

    private SqlRelationSource BindCatalogViewCore(SqlCatalogView view)
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
                    { throw new SqlPlanningException("CS01009", ErrorMessages.ViewFunctionMissing); }
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
                    { throw new SqlPlanningException("CS01009", ErrorMessages.ViewContractChanged); }
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
        var ordering = select.OrderBy.Select(order => order.Expression).ToHashSet();
        foreach (var root in SqlViewQuery.Expressions(select).Where(root => !ordering.Contains(root) &&
            !select.From.SelectMany(item => item.Joins).Any(join => ReferenceEquals(join.Condition, root))))
        { CaptureViewExpression(root, columns, bindings, select.From.FirstOrDefault()?.Table, scope); }
        foreach (var column in select.Columns)
        {
            if (column.Expression is SqlColumnReferenceExpression projected && column.Alias is null &&
                binding.Edits.TryGetValue(projected.Location!.Start, out var rendered) &&
                (rendered.Text.StartsWith("CAST(", StringComparison.Ordinal) || rendered.Text.StartsWith("CASE ", StringComparison.Ordinal)))
            { binding.Edits[projected.Location.Start] = (rendered.Length, rendered.Text + " AS " + SqlObjectIdentity.Quote(projected.ColumnName)); }
            if (column.Expression is not SqlStarExpression star) { continue; }
            var names = new List<string>();
            foreach (int ordinal in outputOrdinals ?? Enumerable.Range(0, columns.Count).ToArray())
            {
                string name = RenderViewColumn(ordinal, columns, bindings, select.From[0].Table, scope, merged: true);
                names.Add(name + " AS " + SqlObjectIdentity.Quote(columns[ordinal].Name));
            }
            binding.Edits[star.Location!.Start] = (star.Location.End - star.Location.Start, string.Join(", ", names));
        }
    }

    private void CaptureViewOrdering(SqlSelectExpression select, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, SqlExpressionEvaluator scope, IReadOnlyDictionary<SqlExpression, int> outputs)
    {
        if (ViewDefinitionBinding is null) { return; }
        foreach (var order in select.OrderBy)
        { CaptureViewExpression(order.Expression, columns, bindings, select.From.FirstOrDefault()?.Table, scope, outputs); }
    }

    private void CaptureViewExpression(SqlExpression expression, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, SqlRelationReference? relation, SqlExpressionEvaluator scope,
        IReadOnlyDictionary<SqlExpression, int>? outputs = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (outputs?.ContainsKey(expression) == true) { return; } // Output dependencies were captured at projection binding.
        if (expression is SqlCastExpression cast && SqlDefinitionBinder.ResolveType(cast.TargetType, _functions, cast.Location) is { IsDomain: true } domain)
        { ViewDefinitionBinding!.Dependencies.Add(SqlDefinitionBinder.DomainKey(domain.Name)); }
        if (expression is SqlColumnReferenceExpression reference)
        {
            int ordinal = scope.ResolveColumn(reference);
            string text = ordinal < 0 ? CaptureOuterViewColumn(reference)
                : RenderViewColumn(ordinal, columns, bindings, relation!, scope, reference.TableAlias is null && reference.SchemaName is null);
            ViewDefinitionBinding!.Edits[reference.Location!.Start] = (reference.Location.End - reference.Location.Start, text);
        }
        foreach (var child in Children(expression)) { CaptureViewExpression(child, columns, bindings, relation, scope, outputs); }
    }

    // Render the resolver's selected source, including USING coercions, rather than the original spelling.
    // PostgreSQL rewriteDefine.c:61 persists the transformed tree; these edits serialize that binding
    // through the existing canonical renderer without adding AST constructors across the assembly boundary.
    private string RenderViewColumn(int ordinal, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, SqlRelationReference relation, SqlExpressionEvaluator scope, bool merged)
    {
        if (merged && scope.MergedColumn(ordinal) is { } output) { return RenderValue(output.Value); }
        var source = bindings?.First(item => ordinal >= item.Offset && ordinal < item.Offset + item.Source.Columns.Count);
        var table = source?.Source.Table;
        relation = source?.Reference ?? relation;
        if (table is null && bindings is null && relation is SqlTableReference named)
        { TryReadTable(named.SchemaName ?? DefaultSchema, named.TableName, out table!); }
        if (table is not null) { RecordViewColumn(new SqlRelationSource(table), ordinal - (source?.Offset ?? 0)); }
        string qualifier = relation.Alias ?? ((SqlTableReference)relation).TableName;
        string result = SqlObjectIdentity.Quote(qualifier) + "." + SqlObjectIdentity.Quote(columns[ordinal].Name);
        return relation is SqlTableReference { Alias: null, SchemaName: { } schema }
            ? SqlObjectIdentity.Quote(schema) + "." + result : result;

        string RenderValue(SqlBoundExpression value) => value switch
        {
            SqlBoundColumn column => RenderViewColumn(column.Ordinal, columns, bindings, relation, scope, merged: false),
            // A CHAR typmod relabel must keep every character. Bare CAST AS CHAR
            // means CHAR(1); this equivalent CASE clears only the output modifier.
            SqlBoundCast { SourceType: DatabaseType.Character, Target.Storage: { Type: DatabaseType.Character, MaxLength: null } } cast
                => "CASE WHEN TRUE THEN " + RenderValue(cast.Operand) + " ELSE NULL END",
            SqlBoundCast cast => "CAST(" + RenderValue(cast.Operand) + " AS " + cast.TargetText + ")",
            _ => throw ViewError(ErrorMessages.ViewRoundTrip),
        };
    }

    private void RecordViewColumn(SqlRelationSource source, int ordinal)
    {
        if (ViewDefinitionBinding is not { } binding || source.Table is not { } table) { return; }
        var column = table.Columns[ordinal];
        string identity = SqlObjectIdentity.Column(table.ObjectId, column.Name);
        if (binding.Owner is { } owner && !owner.Dependencies.Contains(identity, StringComparer.OrdinalIgnoreCase))
        { throw new SqlPlanningException("CS03033", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewColumnChanged, owner.Schema, owner.Name)); }
        binding.Dependencies.Add(identity);
        if (column.DomainName is { } domain) { binding.Dependencies.Add(SqlDefinitionBinder.DomainKey(domain)); }
    }

    private string CaptureOuterViewColumn(SqlColumnReferenceExpression reference)
    {
        for (var outer = OuterScope; outer is not null; outer = outer.Parent)
        {
            int ordinal;
            try { ordinal = outer.Local.ResolveLocalColumn(reference); }
            catch (SqlPlanningException) { continue; }
            var bindings = outer.Bindings ?? outer.Local.RelationBindings;
            return RenderViewColumn(ordinal, outer.Local.Columns, bindings, outer.Local.Relation ?? bindings![0].Reference, outer.Local,
                reference.TableAlias is null && reference.SchemaName is null);
        }
        throw ViewError(ErrorMessages.ViewRoundTrip);
    }

    private void CaptureViewJoin(SqlExpression expression, IReadOnlyList<SqlRelationBinding> bindings, SqlExpressionEvaluator scope)
    {
        if (ViewDefinitionBinding is null) { return; }
        CaptureViewExpression(expression, scope.Columns, bindings, bindings[0].Reference, scope);
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
        { throw new SqlPlanningException("CS04021", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewReadOnly, view.Schema, view.Name), expression.Location); }
    }

    private static bool SameViewOutput(SqlCatalogColumn left, SqlCatalogColumn right)
        => left.Type.Type == right.Type.Type && left.Type.MaxLength == right.Type.MaxLength &&
            left.Type.Precision == right.Type.Precision && left.Type.Scale == right.Type.Scale &&
            left.Collation == right.Collation && string.Equals(left.DomainName, right.DomainName, StringComparison.OrdinalIgnoreCase);

    private static SqlPlanningException ViewError(string message) => new("CS03033", message);
    private sealed record ParsedView(SqlQueryBodyExpression Query);
    internal sealed record ViewPlan(SqlPlan Query, IReadOnlyList<SqlCatalogColumn> Columns, IReadOnlyList<SqlTableReference> SystemRelations);
    [global::System.Diagnostics.DebuggerDisplay("ViewPlans")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewPlans.DebugView))]
    internal sealed class ViewPlans
    {
        internal object Sync { get; } = new();
        internal Dictionary<SqlCatalogView, SqlBindingEntry<ViewPlan>> Entries { get; } = new(ReferenceEqualityComparer.Instance);

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewPlans value)
            {
                _ = value;
            }
        }
    }
    [global::System.Diagnostics.DebuggerDisplay("ViewBinding")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewBinding.DebugView))]
    internal sealed class ViewBinding
    {
        internal SqlCatalogView? Owner { get; init; }
        internal HashSet<ulong> Tables { get; } = new();
        internal HashSet<string> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal SqlViewCallCapture Calls { get; } = new();
        internal Dictionary<int, (int Length, string Text)> Edits { get; } = new();
        internal List<SqlTableReference> SystemRelations { get; } = new();

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewBinding value)
            {
                _ = value;
            }
        }
    }
}
