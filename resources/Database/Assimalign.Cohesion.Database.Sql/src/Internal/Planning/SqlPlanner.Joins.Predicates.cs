using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>Attaches deterministic singleton restrictions without changing ON/WHERE residuals.</summary>
    private SqlJoinPlan PushJoinPredicates(SqlJoinPlan plan)
    {
        var filters = plan.Bindings.Select(_ => new List<SqlBoundExpression>()).ToArray();
        var predicates = plan.Bindings.Select(_ => new Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>>()).ToArray();
        var nullable = new bool[plan.Bindings.Count];
        foreach (var step in plan.Steps) { nullable[step.Binding] = step.Kind == SqlJoinType.LeftOuter; }
        var edges = new List<(int From, int To, Collation Collation)>();
        foreach (var term in JoinConjuncts(plan.Where))
        {
            AddFilter(term, null);
            AddEqualities(term, null);
        }
        foreach (var step in plan.Steps)
        {
            foreach (var term in JoinConjuncts(step.Condition))
            {
                // An ON restriction on a LEFT input is safe only on the added (nullable) side.
                // Later INNER conditions never filter an earlier nullable input below its LEFT.
                AddFilter(term, step);
                AddEqualities(term, step);
            }
        }

        // Each column receives at most one inferred equality. Original restrictions and the full
        // residual still detect contradictions. A queue reaches transitive INNER equalities without
        // repeatedly walking the expression trees or inventing language AST nodes.
        var pending = new Queue<(int Ordinal, object Value)>();
        var known = new HashSet<int>();
        for (int i = 0; i < predicates.Length; i++)
        {
            foreach (var entry in predicates[i])
            {
                var equality = entry.Value.FirstOrDefault(value => value.Op == SqlBinaryOperator.Equal);
                if (equality.Value is not null)
                {
                    int ordinal = plan.Bindings[i].Offset + entry.Key;
                    known.Add(ordinal);
                    pending.Enqueue((ordinal, equality.Value));
                }
            }
        }
        while (pending.TryDequeue(out var constant))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            foreach (var edge in edges)
            {
                if (edge.From != constant.Ordinal || known.Contains(edge.To)) { continue; }
                int target = BindingOf(edge.To);
                int local = edge.To - plan.Bindings[target].Offset;
                if (!TryJoinBound(target, local, SqlBinaryOperator.Equal, constant.Value, edge.Collation)) { continue; }
                known.Add(edge.To);
                object value = predicates[target][local][^1].Value!;
                filters[target].Add(new SqlBoundBinary(SqlBinaryOperator.Equal, new SqlBoundColumn(edge.To),
                    new SqlBoundConstant(value), new SqlBoundCollation(edge.Collation)));
                pending.Enqueue((edge.To, value));
            }
        }

        var inputs = plan.Bindings.Select((binding, i) => new SqlJoinInput(
            binding.Source.Table is { } table ? SelectAccessPath(table, predicates[i]) : SqlScanPath.Instance,
            filters[i].Count == 0 ? null : filters[i].Count == 1 ? filters[i][0] : new SqlBoundLogical(false, filters[i].ToArray()))).ToArray();
        var steps = plan.Steps.ToArray();
        int first = steps.Length > 0 && steps[0].Access?.InnerBinding == 0 ? 1 : 0;
        if (steps.Length > 0 && steps[0].Kind == SqlJoinType.Inner)
        {
            bool Unique(int binding) => inputs[binding].Access is SqlIndexSeekPath seek && seek.Index.IsUnique &&
                seek.EqualityValues.Count == seek.Index.ColumnNames.Count;
            int? preferred = Unique(0) ? 0 : Unique(1) ? 1 : null;
            if (preferred is int driver)
            {
                first = driver;
                var equalities = JoinConjuncts(steps[0].Condition).OfType<SqlBoundBinary>()
                    .Where(term => term.Operator == SqlBinaryOperator.Equal && term.Left is SqlBoundColumn && term.Right is SqlBoundColumn)
                    .Select(term => (((SqlBoundColumn)term.Left).Ordinal, ((SqlBoundColumn)term.Right).Ordinal, term.Collation.Resolve(null))).ToList();
                steps[0] = steps[0] with { Access = SelectJoinAccessPath(plan.Bindings, plan.Columns, equalities, 1, false, driver) };
            }
        }
        return plan with { Inputs = inputs, Steps = steps, FirstBinding = first };

        int BindingOf(int ordinal)
        {
            for (int i = plan.Bindings.Count - 1; i >= 0; i--)
            {
                if (ordinal >= plan.Bindings[i].Offset) { return i; }
            }
            return -1;
        }

        void AddFilter(SqlBoundExpression term, SqlJoinStep? step)
        {
            var columns = new HashSet<int>();
            if (!JoinRestrictionColumns(term, columns) || columns.Count == 0) { return; }
            int binding = BindingOf(columns.First());
            if (columns.Any(ordinal => BindingOf(ordinal) != binding) ||
                (step?.Kind == SqlJoinType.LeftOuter ? binding != step.Binding : nullable[binding])) { return; }
            filters[binding].Add(term);
            CollectBounds(term, binding);
        }

        void CollectBounds(SqlBoundExpression term, int binding)
        {
            if (term is SqlBoundBetween { IsNegated: false } between)
            {
                AddBound(between.LowOperand, between.Low, SqlBinaryOperator.GreaterOrEqual, between.LowCollation);
                AddBound(between.HighOperand, between.High, SqlBinaryOperator.LessOrEqual, between.HighCollation);
            }
            else if (term is SqlBoundBinary binary && binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.LessThan
                or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterOrEqual)
            {
                if (binary.Left is SqlBoundColumn) { AddBound(binary.Left, binary.Right, binary.Operator, binary.Collation); }
                else { AddBound(binary.Right, binary.Left, Flip(binary.Operator), binary.Collation); }
            }

            void AddBound(SqlBoundExpression column, SqlBoundExpression comparand, SqlBinaryOperator op, SqlBoundCollation collation)
            {
                if (column is not SqlBoundColumn key || !collation.IsFixed) { return; }
                var referenced = new HashSet<int>();
                if (!JoinRestrictionColumns(comparand, referenced) || referenced.Count != 0) { return; }
                try
                {
                    TryJoinBound(binding, key.Ordinal - plan.Bindings[binding].Offset, op,
                        ScopelessEvaluator.Evaluate(comparand, []), collation.Resolve(null));
                }
                catch (DatabaseException) { /* Evaluation failures belong to the original residual. */ }
            }
        }

        bool TryJoinBound(int binding, int local, SqlBinaryOperator op, object? value, Collation collation)
        {
            if (value is null || plan.Bindings[binding].Source.Table is not { } table) { return false; }
            var column = table.Columns[local];
            if (column.Type.Type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json &&
                (!collation.IsIndexBacked || collation != (column.Collation ?? _catalog.DefaultCollation))) { return false; }
            if (op != SqlBinaryOperator.Equal && !IsRangeSargable(column.Type.Type)) { return false; }
            object? stored;
            try
            {
                stored = SqlPlanExecutor.CoerceForColumn(value, column);
                if (stored is null || SqlExpressionEvaluator.Compare(value, stored) != 0) { return false; }
            }
            catch (DatabaseException) { return false; }
            if (stored is double number && number == 0d || stored is float single && single == 0f) { return false; }
            if (!predicates[binding].TryGetValue(local, out var values)) { predicates[binding][local] = values = []; }
            values.Add((op, stored));
            return true;
        }

        void AddEqualities(SqlBoundExpression term, SqlJoinStep? step)
        {
            if (term is not SqlBoundBinary { Operator: SqlBinaryOperator.Equal, Left: SqlBoundColumn left, Right: SqlBoundColumn right } equality ||
                !equality.Collation.IsFixed) { return; }
            int a = BindingOf(left.Ordinal), b = BindingOf(right.Ordinal);
            if (a == b || a < 0 || b < 0) { return; }
            var leftColumn = plan.Columns[left.Ordinal];
            var rightColumn = plan.Columns[right.Ordinal];
            if (!CanSeekJoinEquality(leftColumn.Type.Type, rightColumn.Type.Type)) { return; }
            var collation = equality.Collation.Resolve(null);
            if (leftColumn.Type.Type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json ||
                rightColumn.Type.Type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json)
            {
                // CHAR/text cross-type comparison trims a comparand; it is not a transitive
                // equivalence over text keys. Inference needs the same type and collation.
                if (leftColumn.Type.Type != rightColumn.Type.Type || !collation.IsIndexBacked ||
                    collation != (leftColumn.Collation ?? _catalog.DefaultCollation) ||
                    collation != (rightColumn.Collation ?? _catalog.DefaultCollation)) { return; }
            }
            if (step?.Kind == SqlJoinType.LeftOuter)
            {
                if (b == step.Binding && !nullable[a]) { edges.Add((left.Ordinal, right.Ordinal, collation)); }
                if (a == step.Binding && !nullable[b]) { edges.Add((right.Ordinal, left.Ordinal, collation)); }
            }
            else if (!nullable[a] && !nullable[b])
            {
                edges.Add((left.Ordinal, right.Ordinal, collation));
                edges.Add((right.Ordinal, left.Ordinal, collation));
            }
        }
    }

    /// <summary>Only mandatory AND terms move; OR and every lazy scalar remain intact.</summary>
    private static IEnumerable<SqlBoundExpression> JoinConjuncts(SqlBoundExpression? expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundLogical { IsOr: false } logical)
        {
            foreach (var operand in logical.Operands)
            {
                foreach (var term in JoinConjuncts(operand)) { yield return term; }
            }
        }
        else if (expression is not null) { yield return expression; }
    }

    /// <summary>Rejects volatile calls, subqueries, outer references and deferred failures at any depth.</summary>
    private static bool JoinRestrictionColumns(SqlBoundExpression expression, HashSet<int> columns)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case SqlBoundColumn column: columns.Add(column.Ordinal); return true;
            case SqlBoundConstant or SqlBoundParameter: return true;
            case SqlBoundCall call:
                return call.Function.Volatility != SqlFunctionVolatility.Volatile && All(call.Arguments);
            case SqlBoundCast cast:
                // Casts have no volatility declaration. Only standard conversions are known
                // deterministic; application/domain conversions remain at their original level.
                return !cast.Target.IsDomain && cast.Casts.All(candidate => candidate is null ||
                    ReferenceEquals(candidate, SqlFunctionEnvironment.Standard.Types.ResolveCast(null, candidate.Source.Storage.Type, candidate.Target))) &&
                    JoinRestrictionColumns(cast.Operand, columns);
            case SqlBoundLogical logical: return All(logical.Operands);
            case SqlBoundBinary binary: return Visit(binary.Left) && Visit(binary.Right);
            case SqlBoundUnary unary: return Visit(unary.Operand);
            case SqlBoundDistinct distinct: return Visit(distinct.Left) && Visit(distinct.Right);
            case SqlBoundBooleanTest test: return Visit(test.Operand);
            case SqlBoundIsNull isNull: return Visit(isNull.Operand);
            case SqlBoundBetween between: return Visit(between.LowOperand) && Visit(between.HighOperand) && Visit(between.Low) && Visit(between.High);
            case SqlBoundIn member: return Visit(member.Operand) && All(member.Candidates);
            case SqlBoundLike like: return Visit(like.Operand) && Visit(like.Pattern) && Visit(like.Escape);
            case SqlBoundSpecialForm form: return All(form.Arguments);
            case SqlBoundCoalesce coalesce: return All(coalesce.Arguments);
            case SqlBoundCase choice:
                return (choice.Input is null || Visit(choice.Input)) &&
                    choice.Whens.All(when => Visit(when.Condition) && Visit(when.Result)) &&
                    (choice.ElseResult is null || Visit(choice.ElseResult));
            default: return false;
        }

        bool Visit(SqlBoundExpression child) => JoinRestrictionColumns(child, columns);
        bool All(IEnumerable<SqlBoundExpression> children) => children.All(Visit);
    }
}
