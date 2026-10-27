using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static bool IsMergeAction(SqlFunctionCallExpression call)
        => call.FunctionName.Equals("MERGE_ACTION", StringComparison.OrdinalIgnoreCase);

    private SqlPlan PlanMerge(SqlMergeExpression merge)
    {
        var (table, evaluator, source, queries) = BindDmlSources(merge.Table, [merge.Source], merge.Condition, merge, merge: true);
        var associations = source!.Associations;
        if (merge.Actions.Count == 0) { throw Invalid(); }
        var terminal = new HashSet<SqlMergeMatchKind>();
        var actions = new List<SqlMergeActionPlan>();
        foreach (var clause in merge.Actions)
        {
            if (!Enum.IsDefined(clause.MatchKind) || !Enum.IsDefined(clause.Action) ||
                clause.MatchKind == SqlMergeMatchKind.NotMatchedByTarget && clause.Action is SqlMergeAction.Update or SqlMergeAction.Delete ||
                clause.MatchKind != SqlMergeMatchKind.NotMatchedByTarget && clause.Action == SqlMergeAction.Insert) { throw Invalid(); }
            if (terminal.Contains(clause.MatchKind))
            { throw new SqlPlanningException("CS05071", ErrorMessages.MergeWhenUnreachable, clause.Location); }
            if (clause.Condition is null) { terminal.Add(clause.MatchKind); }
            // PostgreSQL parse_merge.c:52-99: unmatched actions cannot name the absent side.
            var scope = clause.MatchKind switch
            {
                SqlMergeMatchKind.NotMatchedByTarget => evaluator.ForRelations(associations.Bindings.Skip(1).ToArray()),
                SqlMergeMatchKind.NotMatchedBySource => evaluator.ForRelations([associations.Bindings[0]]),
                _ => evaluator,
            };
            SqlBoundExpression? condition = null;
            var actionExpressions = clause.Assignments.Select(assignment => assignment.Value)
                .Concat(clause.Values ?? []).ToList();
            if (clause.Condition is { } predicate) { actionExpressions.Insert(0, predicate); }
            var actionQueries = BindMergeActionQueries(actionExpressions, merge.Table, scope);
            if (clause.Condition is { } filter)
            {
                // Keep these with the clause: SQ-M2 must not materialize a later action's
                // subqueries before an earlier action has decided whether to handle the row.
                ValidateMergeValue(filter, scope, "MERGE WHEN");
                condition = scope.BindPredicate(filter);
            }
            var assignments = new List<(int Ordinal, SqlBoundExpression Value)>();
            var assigned = new HashSet<int>();
            foreach (var assignment in clause.Assignments)
            {
                int ordinal = TargetOrdinal(assignment.ColumnName, assignment.Value);
                ValidateIdentity(ordinal, assignment.Value, update: true);
                assignments.Add((ordinal, BindValue(ordinal, assignment.Value, scope)));
            }
            var ordinals = new List<int>();
            if (clause.Action == SqlMergeAction.Insert)
            {
                foreach (string name in clause.Columns ?? table.Columns.Select(column => column.Name).ToArray())
                { ordinals.Add(TargetOrdinal(name, merge.Condition)); }
                if (!clause.IsDefaultValues && (clause.Values is null || clause.Values.Count != ordinals.Count))
                { throw new SqlPlanningException("CS05013", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.InsertRowWidth, clause.Values?.Count ?? 0, ordinals.Count), merge.Location); }
            }
            var values = new List<SqlBoundExpression>();
            if (!clause.IsDefaultValues && clause.Values is { } insertValues)
            {
                for (int i = 0; i < insertValues.Count; i++)
                {
                    ValidateIdentity(ordinals[i], insertValues[i], update: false);
                    values.Add(BindValue(ordinals[i], insertValues[i], scope));
                }
            }
            actions.Add(new(clause.MatchKind, clause.Action, condition, assignments, ordinals, values, clause.IsDefaultValues, actionQueries));

            int TargetOrdinal(string name, SqlExpression expression)
            {
                int ordinal = FindColumnOrdinal(table, name);
                if (!assigned.Add(ordinal))
                { throw new SqlPlanningException("CS01019", string.Format(CultureInfo.CurrentCulture, ErrorMessages.DuplicateAssignment, name), expression.Location); }
                return ordinal;
            }
        }
        ValidateMergeValue(merge.Condition, evaluator, "MERGE ON");
        var on = evaluator.BindPredicate(merge.Condition);
        // PostgreSQL prepjointree.c:232-246 ignores DO NOTHING when choosing preserved sides.
        bool byTarget = actions.Any(action => action.MatchKind == SqlMergeMatchKind.NotMatchedByTarget && action.Action != SqlMergeAction.DoNothing);
        bool bySource = actions.Any(action => action.MatchKind == SqlMergeMatchKind.NotMatchedBySource && action.Action != SqlMergeAction.DoNothing);
        var kind = bySource ? byTarget ? SqlJoinType.FullOuter : SqlJoinType.LeftOuter
            : byTarget ? SqlJoinType.RightOuter : SqlJoinType.Inner;
        var root = associations.Tree!;
        var step = root.Step! with { Kind = kind, Condition = on };
        if (kind == SqlJoinType.FullOuter)
        {
            // Match #1525's FULL JOIN eligibility, including the complete ON safety proof.
            // PostgreSQL prepjointree.c:238-240 chooses FULL; joinrels.c:1165-1179
            // refuses it when neither a merge nor a hash path is available.
            var keys = HashKeys(on, associations.Bindings, associations.Columns, 1, associations.Bindings.Count - 1, fullFrame: true);
            if (keys is null)
            {
                throw new SqlPlanningException("CS05070", ErrorMessages.FullJoinCondition, merge.Condition.Location);
            }
            step = step with { Hash = keys };
        }
        var columns = associations.Columns.Select((column, ordinal) =>
            (ordinal < table.Columns.Count ? byTarget : bySource) ? NullableColumn(column) : column).ToArray();
        // USING outputs carry metadata separately from physical slots. An absent source
        // nullifies those merged outputs too, even if both original key inputs were NOT NULL.
        var bindings = associations.Bindings.Select((binding, index) => bySource && index > 0 &&
            binding.UsingColumns is { Count: > 0 } merged ? binding with
            {
                UsingColumns = merged.ToDictionary(pair => pair.Key,
                    pair => pair.Value with { Column = NullableColumn(pair.Value.Column) }),
            } : binding).ToArray();
        associations = associations with
        {
            Bindings = bindings,
            Columns = columns,
            Projections = associations.Projections.Select((projection, ordinal) =>
                projection with { IsNullable = columns[ordinal].IsNullable }).ToArray(),
            Steps = associations.Steps.Select(existing => ReferenceEquals(existing, root.Step) ? step : existing).ToArray(),
            Tree = kind == SqlJoinType.RightOuter ? new(-1, root.Right, root.Left, step) : root with { Step = step },
        };
        var input = source with { Associations = PushJoinPredicates(associations) };
        return WrapSubqueries(new SqlMergePlan(table, input, actions, BindMergeReturning(merge, input), columns.Length), queries);

        static SqlCatalogColumn NullableColumn(SqlCatalogColumn column) => column.IsNullable ? column
            : new SqlCatalogColumn(column.Name, column.Type, true, column.DefaultLiteral,
                column.Collation, column.DomainName, column.DefaultFunctionCalls);

        void ValidateIdentity(int ordinal, SqlExpression expression, bool update)
        {
            if (table.Columns[ordinal].Identity is { IsAlways: true } && (update || expression is not SqlDefaultExpression))
            { throw SqlPlanningException.IdentityViolation(table.Columns[ordinal].Name, update ? ErrorMessages.IdentityUpdateForbidden : ErrorMessages.IdentityInsertForbidden); }
        }

        SqlBoundExpression BindValue(int ordinal, SqlExpression expression, SqlExpressionEvaluator scope)
        {
            if (expression is SqlDefaultExpression) { return SqlBoundDefaultItem.Instance; }
            ValidateMergeValue(expression, scope);
            var type = scope.StaticTypeOf(expression);
            var target = table.Columns[ordinal];
            if (!CanAssignInsertType(type, target.Type.Type))
            { throw new SqlPlanningException("CS02003", string.Format(CultureInfo.CurrentCulture, ErrorMessages.MergeAssignmentType, type, target.Name, target.Type.Type), expression.Location); }
            return scope.Bind(expression);
        }

        SqlPlanningException Invalid() => new("CS05013", ErrorMessages.MergeActionInvalid, merge.Location);
    }

    private bool FoldQueryConstants => _queryScopes?.DeferQueryEvaluation != true;

    private IReadOnlyList<SqlSubqueryBinding> BindMergeActionQueries(IEnumerable<SqlExpression> expressions,
        SqlTableReference target, SqlExpressionEvaluator evaluator)
    {
        var state = _queryScopes!;
        bool deferred = state.DeferQueryEvaluation;
        var scopeless = _scopelessEvaluator;
        state.DeferQueryEvaluation = true;
        _scopelessEvaluator = null;
        try
        {
            return BindSubqueries(expressions.SelectMany(WalkSubqueries), dml: target,
                scope: new SqlCorrelationScope(NextCorrelationScope(), evaluator, OuterScope));
        }
        finally
        {
            state.DeferQueryEvaluation = deferred;
            _scopelessEvaluator = scopeless;
        }
    }

    private void ValidateMergeValue(SqlExpression expression, SqlExpressionEvaluator scope, string? predicate = null)
    {
        if (ContainsAggregate(expression)) { throw new SqlPlanningException("CS05017", ErrorMessages.MergeAggregate, expression.Location); }
        if (ContainsStar(expression)) { throw new SqlPlanningException("CS05013", ErrorMessages.MergeStar, expression.Location); }
        if (predicate is null) { ValidateExpression(expression, scope, _subqueryTypes); }
        else { ValidatePredicate(expression, scope, predicate, _subqueryTypes); }
    }

    private IReadOnlyList<SqlProjection>? BindMergeReturning(SqlMergeExpression merge, SqlDmlSource source)
    {
        if (merge.Returning is null) { return null; }
        var actionSlots = new Dictionary<SqlExpression, int>(ReferenceEqualityComparer.Instance);
        foreach (var column in merge.Returning)
        {
            if (ContainsAggregate(column.Expression))
            { throw new SqlPlanningException("CS05017", ErrorMessages.ReturningAggregate, column.Expression.Location); }
            Find(column.Expression);
        }
        var associations = source.Associations;
        // Every changed row has an old or new target for RETURNING, including INSERT.
        // Only the association frame before action selection can lack the target.
        var columns = associations.Columns.ToArray();
        var targetColumns = associations.Bindings[source.TargetBinding].Source.Columns;
        for (int i = 0; i < targetColumns.Count; i++) { columns[i] = targetColumns[i]; }
        var scope = DmlEvaluator(columns, associations.Bindings, merge.Table, foldConstants: false)
            .ForOrdering([new SqlProjection("merge_action", null, null, DatabaseType.String)], actionSlots, associations.Columns.Count);
        return BindProjections(merge.Returning, columns, scope, source.VisibleOrdinals);

        void Find(SqlExpression expression)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlFunctionCallExpression call && IsMergeAction(call)) { actionSlots.Add(call, 0); }
            foreach (var child in Children(expression)) { Find(child); }
        }
    }
}
