using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>Attaches proven non-throwing singleton restrictions without changing ON/WHERE residuals.</summary>
    private SqlJoinPlan PushJoinPredicates(SqlJoinPlan plan, bool pinFirst = false)
    {
        if (plan.Tree is not null || _queryScopes?.DeferQueryEvaluation == true)
        {
            // Restrictions retain their written level across grouped and swapped boundaries.
            return PlanPartialJoinAccess(plan with { Inputs = plan.Bindings.Select(_ => new SqlJoinInput(SqlScanPath.Instance, null)).ToArray() });
        }
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
            int? preferred = pinFirst || Unique(0) ? 0 : Unique(1) ? 1 : null;
            if (preferred is int driver)
            {
                first = driver;
                var equalities = JoinConjuncts(steps[0].Condition).OfType<SqlBoundBinary>()
                    .Where(term => term.Operator == SqlBinaryOperator.Equal && term.Left is SqlBoundColumn && term.Right is SqlBoundColumn)
                    .Select(term => (((SqlBoundColumn)term.Left).Ordinal, ((SqlBoundColumn)term.Right).Ordinal, term.Collation.Resolve(null))).ToList();
                steps[0] = steps[0] with { Access = SelectJoinAccessPath(plan.Bindings, plan.Columns, equalities, 1, false, driver) };
            }
        }
        return PlanPartialJoinAccess(plan with { Inputs = inputs, Steps = steps, FirstBinding = first });

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
            if (!JoinRestrictionColumns(term, columns, plan.Columns) || columns.Count == 0) { return; }
            int binding = BindingOf(columns.First());
            if (columns.Any(ordinal => BindingOf(ordinal) != binding) ||
                (step?.Kind == SqlJoinType.LeftOuter ? binding != step.Binding : nullable[binding])) { return; }
            // Materialized derived text can come straight from parameters/application calls,
            // without the stored-row codec's valid-Unicode guarantee. Its collation may throw.
            if (plan.Bindings[binding].Source.Derived is not null &&
                columns.Any(ordinal => TextOrNull(plan.Columns[ordinal].Type.Type))) { return; }
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
                if (!JoinRestrictionColumns(comparand, referenced, plan.Columns) || referenced.Count != 0) { return; }
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

    /// <summary>Only expressions proven non-throwing for their operand types may run before a join.</summary>
    internal static bool JoinRestrictionColumns(SqlBoundExpression expression, HashSet<int> columns, IReadOnlyList<SqlCatalogColumn> schema)
        => JoinRestrictionType(expression, columns, schema) is not null;

    // Volatility alone says nothing about errors. Keep an explicit allowlist, including each
    // comparison's type compatibility and collation, rather than accepting arbitrary binary
    // operators, standard casts or immutable application calls.
    private static DatabaseType? JoinRestrictionType(SqlBoundExpression expression, HashSet<int> columns, IReadOnlyList<SqlCatalogColumn> schema)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case SqlBoundColumn column:
                columns.Add(column.Ordinal);
                return schema[column.Ordinal].Type.Type is DatabaseType.Null ? null : schema[column.Ordinal].Type.Type;
            case SqlBoundConstant constant: return ValueType(constant.Value);
            case SqlBoundParameter parameter: return ValueType(parameter.Value);
            case SqlBoundCast cast:
                var source = TypeOf(cast.Operand);
                var target = cast.Target.Storage;
                if (source is null || cast.Target.IsDomain || target.MaxLength is not null ||
                    target.Precision is not null || target.Scale is not null || !Widening(source.Value, target.Type)) { return null; }
                var conversion = cast.Casts[(int)source.Value];
                return conversion is null ? source == target.Type || source == DatabaseType.Null ? target.Type : null
                    : SqlStandardCasts.Contains(conversion) ? target.Type : null;
            case SqlBoundCoalesce coalesce:
                return coalesce.Arguments.All(argument => TypeOf(argument) == coalesce.ResultType)
                    ? coalesce.ResultType : null;
            case SqlBoundCall call:
                // Clock calls retain the statement's captured transaction instant at both
                // planning and execution. No other Stable/Immutable declaration proves safety.
                if (call.Function is SqlBuiltinScalarFunction && call.Arguments.Length == 0 && call.TransactionStart is not null &&
                    call.Function.Name is "CURRENT_DATE" or "CURRENT_TIME" or "CURRENT_TIMESTAMP" or "LOCALTIME" or "LOCALTIMESTAMP" or "NOW")
                {
                    return call.Function.ReturnType.Storage.Type;
                }
                if ((call.Function is SqlLengthFunction || call.Function is SqlBuiltinScalarFunction { Name: "CHAR_LENGTH" or "CHARACTER_LENGTH" }) &&
                    call.Arguments.Length == 1 && TextOrNull(TypeOf(call.Arguments[0]))) { return DatabaseType.Int64; }
                return null;
            case SqlBoundBinary binary when binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
                or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessOrEqual or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterOrEqual:
                return Comparison(binary.Left, binary.Right, binary.Collation) ? DatabaseType.Boolean : null;
            case SqlBoundDistinct distinct:
                return Comparison(distinct.Left, distinct.Right, distinct.Collation) ? DatabaseType.Boolean : null;
            case SqlBoundIsNull isNull: return TypeOf(isNull.Operand) is not null ? DatabaseType.Boolean : null;
            case SqlBoundBooleanTest test:
                return BooleanOrNull(TypeOf(test.Operand)) ? DatabaseType.Boolean : null;
            case SqlBoundUnary { Operator: SqlUnaryOperator.Not } unary:
                return BooleanOrNull(TypeOf(unary.Operand)) ? DatabaseType.Boolean : null;
            case SqlBoundLogical logical:
                return logical.Operands.All(operand => BooleanOrNull(TypeOf(operand))) ? DatabaseType.Boolean : null;
            case SqlBoundBetween between:
                return Comparison(between.LowOperand, between.Low, between.LowCollation) &&
                    Comparison(between.HighOperand, between.High, between.HighCollation) ? DatabaseType.Boolean : null;
            case SqlBoundIn member:
                var operand = TypeOf(member.Operand);
                return operand is not null && member.Candidates.Select((candidate, i) =>
                    Comparable(operand, TypeOf(candidate), member.Collations[i])).All(safe => safe) ? DatabaseType.Boolean : null;
            case SqlBoundLike like:
                // Row-dependent patterns/escapes can be malformed. Binding validates and
                // compiles constant/parameter patterns; only those have a safety proof.
                return like.Compiled is not null && like.Collation.IsFixed && like.Collation.Resolve(null).IsIndexBacked &&
                    TextOrNull(TypeOf(like.Operand)) && TextOrNull(TypeOf(like.Pattern)) && TextOrNull(TypeOf(like.Escape))
                    ? DatabaseType.Boolean : null;
            default: return null;
        }

        DatabaseType? TypeOf(SqlBoundExpression child) => JoinRestrictionType(child, columns, schema);
        bool Comparison(SqlBoundExpression left, SqlBoundExpression right, SqlBoundCollation collation)
            => Comparable(TypeOf(left), TypeOf(right), collation);

        static DatabaseType? ValueType(object? value)
        {
            // Parameters need not have a supported CLR type, and malformed UTF-16 must not
            // raise a collation error earlier merely because its comparison was pushed.
            try
            {
                if (value is string text) { Collation.Binary.Normalize(text); }
                if (value is SqlCharacter character) { Collation.Binary.Normalize(character.Value); }
                return value switch
                {
                    null => DatabaseType.Null,
                    SqlCharacter => DatabaseType.Character,
                    string => DatabaseType.String,
                    sbyte => DatabaseType.Int8, short => DatabaseType.Int16, int => DatabaseType.Int32, long => DatabaseType.Int64,
                    float => DatabaseType.Float32, double => DatabaseType.Float64, decimal => DatabaseType.Decimal,
                    bool => DatabaseType.Boolean, byte[] => DatabaseType.Binary,
                    DateOnly => DatabaseType.Date, TimeOnly => DatabaseType.Time, DateTime => DatabaseType.DateTime,
                    DateTimeOffset => DatabaseType.DateTimeOffset, TimeSpan => DatabaseType.TimeSpan, Guid => DatabaseType.Guid,
                    _ => null,
                };
            }
            catch (DatabaseTypeException) { return null; }
        }
    }

    private static bool BooleanOrNull(DatabaseType? type) => type is DatabaseType.Boolean or DatabaseType.Null;

    private static bool TextOrNull(DatabaseType? type) => type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json or DatabaseType.Null;

    private static bool Comparable(DatabaseType? left, DatabaseType? right, SqlBoundCollation collation)
    {
        if (left is null || right is null || !collation.IsFixed) { return false; }
        if (TextOrNull(left) && TextOrNull(right)) { return collation.Resolve(null).IsIndexBacked; }
        if (left == DatabaseType.Null || right == DatabaseType.Null) { return true; }
        if (SqlFunctionResolver.IsNumeric(left.Value) && SqlFunctionResolver.IsNumeric(right.Value)) { return true; }
        if (left is DatabaseType.Date or DatabaseType.DateTime or DatabaseType.DateTimeOffset &&
            right is DatabaseType.Date or DatabaseType.DateTime or DatabaseType.DateTimeOffset) { return true; }
        return left == right && left is DatabaseType.Boolean or DatabaseType.Binary or DatabaseType.JsonBinary
            or DatabaseType.Time or DatabaseType.TimeSpan or DatabaseType.Guid;
    }

    private static bool Widening(DatabaseType source, DatabaseType target)
        => source == DatabaseType.Null || source == target || source switch
        {
            DatabaseType.Int8 => target is DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Decimal,
            DatabaseType.Int16 => target is DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Decimal,
            DatabaseType.Int32 => target is DatabaseType.Int64 or DatabaseType.Decimal,
            DatabaseType.Int64 => target == DatabaseType.Decimal,
            DatabaseType.Float32 => target == DatabaseType.Float64,
            _ => false,
        };

    /// <summary>Proves and extracts mandatory hash keys for binding and optional physical costing.</summary>
    internal static SqlHashJoinPath? HashKeys(SqlBoundExpression? condition, IReadOnlyList<SqlRelationBinding> bindings,
        IReadOnlyList<SqlCatalogColumn> columns, int inner, int? lastInner = null, bool fullFrame = false, int innerMask = 0)
    {
        // Skipping nonmatching pairs must not suppress errors, application calls or lazy scalars.
        if (condition is null) { return null; }
        var referenced = new HashSet<int>();
        if (!JoinRestrictionColumns(condition, referenced, columns)) { return null; }
        // Every ON term is skipped for nonmatching hash keys. Derived text has no stored
        // codec guarantee of valid Unicode, even when it appears only in a residual term.
        if (bindings.Any(binding => binding.Source.Derived is not null && referenced.Any(ordinal =>
            ordinal >= binding.Offset && ordinal < binding.Offset + binding.Source.Columns.Count &&
            columns[ordinal].Type.Type is DatabaseType.String or DatabaseType.Json or DatabaseType.Character))) { return null; }
        var binding = bindings[inner];
        var end = bindings[lastInner ?? inner];
        int endOrdinal = end.Offset + end.Source.Columns.Count;
        var outer = new List<int>();
        var local = new List<int>();
        var collations = new List<Collation>();
        List<SqlBoundExpression>? outerExpressions = null;
        List<SqlBoundExpression>? innerExpressions = null;
        foreach (var term in Terms(condition))
        {
            if (term is not SqlBoundBinary { Operator: SqlBinaryOperator.Equal } equality) { continue; }
            SqlBoundExpression source, probe;
            if (equality.Left is SqlBoundColumn a && equality.Right is SqlBoundColumn b)
            {
                // Keep column-only extraction on its original ordinal path, without expression analysis.
                bool leftInner = innerMask == 0 ? a.Ordinal >= binding.Offset && a.Ordinal < endOrdinal : InsideJoinMask(a.Ordinal, bindings, innerMask);
                bool rightInner = innerMask == 0 ? b.Ordinal >= binding.Offset && b.Ordinal < endOrdinal : InsideJoinMask(b.Ordinal, bindings, innerMask);
                if (leftInner == rightInner || columns[a.Ordinal].Type.Type == DatabaseType.Character ||
                    columns[b.Ordinal].Type.Type == DatabaseType.Character) { continue; }
                source = leftInner ? a : b;
                probe = leftInner ? b : a;
            }
            else
            {
                if (!fullFrame || !TryHashExpressionKey(equality, columns, bindings, binding.Offset, endOrdinal, innerMask, out bool leftInner)) { continue; }
                source = leftInner ? equality.Left : equality.Right;
                probe = leftInner ? equality.Right : equality.Left;
                // Backfill earlier column keys only when this side first needs an expression vector.
                if (probe is not SqlBoundColumn)
                {
                    outerExpressions ??= outer.Select(static ordinal => (SqlBoundExpression)new SqlBoundColumn(ordinal)).ToList();
                }
                if (source is not SqlBoundColumn)
                {
                    innerExpressions ??= local.Select(static ordinal => (SqlBoundExpression)new SqlBoundColumn(ordinal)).ToList();
                }
            }
            outer.Add(probe is SqlBoundColumn outerColumn ? outerColumn.Ordinal : -1);
            local.Add(source is SqlBoundColumn innerColumn ? fullFrame ? innerColumn.Ordinal : innerColumn.Ordinal - binding.Offset : -1);
            outerExpressions?.Add(probe);
            innerExpressions?.Add(source);
            collations.Add(equality.Collation.Resolve(null));
        }
        return outer.Count == 0 ? null : new(outer.ToArray(), local.ToArray(), collations.ToArray(),
            outerExpressions?.ToArray(), innerExpressions?.ToArray());

        static IEnumerable<SqlBoundExpression> Terms(SqlBoundExpression expression)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlBoundLogical { IsOr: false } logical)
            { foreach (var operand in logical.Operands) { foreach (var item in Terms(operand)) { yield return item; } } }
            else { yield return expression; }
        }
    }

    private static bool InsideJoinMask(int ordinal, IReadOnlyList<SqlRelationBinding> bindings, int mask)
    {
        for (int i = bindings.Count - 1; i >= 0; i--)
        { if (ordinal >= bindings[i].Offset) { return (mask & (1 << i)) != 0; } }
        return false;
    }

    private static bool TryHashExpressionKey(SqlBoundBinary equality, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding> bindings, int startOrdinal, int endOrdinal, int innerMask, out bool leftInner)
    {
        var left = new HashSet<int>();
        var right = new HashSet<int>();
        leftInner = false;
        if (!JoinRestrictionColumns(equality.Left, left, columns) ||
            !JoinRestrictionColumns(equality.Right, right, columns) || left.Count == 0 || right.Count == 0) { return false; }
        bool Inside(int ordinal) => innerMask == 0 ? ordinal >= startOrdinal && ordinal < endOrdinal
            : InsideJoinMask(ordinal, bindings, innerMask);
        leftInner = left.All(Inside);
        bool rightInner = right.All(Inside);
        if (leftInner == rightInner || !(leftInner ? right : left).All(ordinal => !Inside(ordinal))) { return false; }
        // CHAR/text comparison trims a comparand and is not a hash equivalence over text.
        return !left.Concat(right).Any(ordinal => columns[ordinal].Type.Type == DatabaseType.Character);
    }
}
