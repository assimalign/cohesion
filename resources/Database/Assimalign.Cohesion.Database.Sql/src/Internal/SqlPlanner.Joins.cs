using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>Binds every step before execution, with ON visibility limited to its comma item.</summary>
    private IReadOnlyList<SqlRelationBinding> BindJoin(SqlSelectExpression select, SqlRelationSource first,
        out IReadOnlyList<SqlCatalogColumn> columns, out IReadOnlyList<SqlJoinStep> steps,
        out IReadOnlyList<int> outputOrdinals, bool metadataOnly = false)
    {
        var bindings = new List<SqlRelationBinding>();
        var allColumns = new List<SqlCatalogColumn>();
        var allSteps = new List<SqlJoinStep>();
        var output = new List<int>();
        foreach (var item in select.From)
        {
            int start = bindings.Count;
            Add(item.Table, false);
            var visible = Enumerable.Range(bindings[start].Offset, bindings[start].Source.Columns.Count).ToList();
            if (start > 0) { allSteps.Add(new SqlJoinStep(start, SqlJoinType.Cross, null, null)); }
            foreach (var join in item.Joins)
            {
                Add(join.Table, join.JoinType == SqlJoinType.LeftOuter);
                int rightIndex = bindings.Count - 1;
                var right = bindings[rightIndex];
                var scope = new SqlExpressionEvaluator(allColumns, _parameters, bindings.Skip(start).ToArray(),
                    defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots,
                    functions: _functions, cancellationToken: _cancellationToken,
                    outerScope: OuterScope, outerDependencies: OuterDependencies);
                var equalities = new List<(int Left, int Right, Collation Collation)>();
                SqlBoundExpression? condition = null;
                if (join.Condition is { } on)
                {
                    if (ContainsAggregate(on))
                    {
                        if (!metadataOnly) { throw new SqlPlanningException("COHSQLE017", "Aggregate functions are not allowed in JOIN ON.", on.Location); }
                    }
                    if (!metadataOnly)
                    {
                        ValidateExpression(on, scope, _subqueryTypes);
                        condition = scope.Bind(on);
                        CollectJoinEqualities(on, scope, equalities);
                    }
                }
                else if (join.UsingColumns.Count > 0)
                {
                    var merged = new List<int>();
                    var predicates = new List<SqlBoundExpression>();
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in join.UsingColumns)
                    {
                        if (!names.Add(name))
                        {
                            throw new SqlPlanningException("COHSQLE015", $"Column '{name}' appears more than once in USING.", select.Location);
                        }
                        int leftOrdinal = FindUsing(visible, name, "left");
                        int rightOrdinal = FindUsing(Enumerable.Range(right.Offset, right.Source.Columns.Count), name, "right");
                        var previous = scope.MergedColumn(leftOrdinal);
                        var leftColumn = previous?.Column ?? allColumns[leftOrdinal];
                        var rightColumn = allColumns[rightOrdinal];
                        var common = UsingType(leftColumn.Type, rightColumn.Type, name, select);
                        bool sameDomain = string.Equals(leftColumn.DomainName, rightColumn.DomainName, StringComparison.OrdinalIgnoreCase);
                        if (!sameDomain) { common = new DatabaseTypeInfo(common.Type); }
                        var leftValue = previous?.Value ?? new SqlBoundColumn(leftOrdinal);
                        var rightValue = new SqlBoundColumn(rightOrdinal);
                        // PostgreSQL parse_clause.c:1676-1709,1718-1733: choose a common type,
                        // coerce the inputs, prefer an unchanged INNER input, and preserve LEFT.
                        var value = CoerceUsing(leftValue, leftColumn.Type.Type, common);
                        if (join.JoinType == SqlJoinType.Inner && leftColumn.Type.Type != common.Type
                            && rightColumn.Type.Type == common.Type)
                        {
                            value = rightValue;
                        }
                        var collation = SqlBoundCollation.Of(
                            new SqlCollationCandidate(leftColumn.Collation ?? _catalog.DefaultCollation, leftColumn.Collation is null ? 1 : 2),
                            new SqlCollationCandidate(rightColumn.Collation ?? _catalog.DefaultCollation, rightColumn.Collation is null ? 1 : 2),
                            _catalog.DefaultCollation);
                        predicates.Add(new SqlBoundBinary(SqlBinaryOperator.Equal, leftValue, rightValue, collation));
                        if (leftValue is SqlBoundColumn physical)
                        {
                            equalities.Add((physical.Ordinal, rightOrdinal, collation.Resolve(null)));
                        }
                        var owner = bindings.Last(binding => binding.Offset <= leftOrdinal);
                        owner.UsingColumns ??= [];
                        owner.UsingColumns[leftOrdinal] = new SqlUsingColumn(
                            new SqlCatalogColumn(name, common, leftColumn.IsNullable, collation: leftColumn.Collation,
                                // PostgreSQL parse_coerce.c:1360-1387 preserves identical domains.
                                domainName: sameDomain ? leftColumn.DomainName : null), value);
                        merged.Add(leftOrdinal);
                        right.HiddenColumns.Add(rightOrdinal - right.Offset);
                    }
                    condition = predicates.Count == 1 ? predicates[0] : new SqlBoundLogical(false, predicates.ToArray());
                    visible = merged.Concat(visible.Except(merged)).ToList();
                }
                if (!metadataOnly && (!SqlLanguageProfile.SupportsJoin(join.JoinType) ||
                    (join.JoinType != SqlJoinType.Cross && condition is null) ||
                    (join.JoinType == SqlJoinType.Cross && condition is not null)))
                {
                    throw new SqlPlanningException("COHSQLE013", "INNER and LEFT JOIN require ON or USING; CROSS JOIN accepts neither.", select.Location);
                }
                visible.AddRange(Enumerable.Range(right.Offset, right.Source.Columns.Count)
                    .Where(ordinal => !right.HiddenColumns.Contains(ordinal - right.Offset)));
                var access = SelectJoinAccessPath(bindings, allColumns, equalities, rightIndex,
                    rightIndex == 1 && start == 0 && join.JoinType == SqlJoinType.Inner);
                allSteps.Add(new SqlJoinStep(rightIndex, join.JoinType, condition, access));
            }
            output.AddRange(visible);
        }
        columns = allColumns;
        steps = allSteps;
        outputOrdinals = output;
        return bindings;

        int FindUsing(IEnumerable<int> ordinals, string name, string side)
        {
            var found = ordinals.Where(i => string.Equals(allColumns[i].Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length != 1)
            {
                throw new SqlPlanningException("COHSQLE015", $"USING column '{name}' is {(found.Length == 0 ? "missing" : "ambiguous")} on the {side} input.", select.Location);
            }
            return found[0];
        }

        void Add(SqlRelationReference reference, bool nullable)
        {
            var source = bindings.Count == 0 ? first : ResolveRelation(reference, select);
            string name = reference.Alias ?? source.Name;
            if (bindings.Any(binding => string.Equals(name, binding.Reference.Alias ?? binding.Source.Name, StringComparison.OrdinalIgnoreCase) &&
                (reference.Alias is not null || binding.Reference.Alias is not null ||
                    source.Derived is { IsCommonTable: true } || binding.Source.Derived is { IsCommonTable: true } ||
                    string.Equals(source.Schema, binding.Source.Schema, StringComparison.OrdinalIgnoreCase))))
            {
                throw new SqlPlanningException("COHSQLE019", $"Duplicate table name or alias '{name}'; use distinct aliases for a self join.", select.Location);
            }
            bindings.Add(new SqlRelationBinding(source, reference, allColumns.Count));
            allColumns.AddRange(source.Columns.Select(column => nullable && !column.IsNullable
                ? new SqlCatalogColumn(column.Name, column.Type, true, column.DefaultLiteral, column.Collation, column.DomainName, column.DefaultFunctionCalls)
                : column));
        }
    }

    /// <summary>Selects the common USING type, retaining modifiers only when both inputs agree.</summary>
    private DatabaseTypeInfo UsingType(DatabaseTypeInfo left, DatabaseTypeInfo right, string name, SqlQueryBodyExpression select, bool setOperation = false)
    {
        var common = left.Type;
        if (left.Type != right.Type)
        {
            // PostgreSQL parse_coerce.c:1393-1437: categories must match; switch candidates
            // only for a one-way implicit conversion. CHAR/VARCHAR both convert implicitly.
            static int Category(DatabaseType type) => type switch
            {
                DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64
                    or DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64 => 1,
                DatabaseType.Character or DatabaseType.String => 2,
                DatabaseType.Date or DatabaseType.DateTime or DatabaseType.DateTimeOffset => 3,
                _ => 0,
            };
            bool CanConvert(DatabaseType from, DatabaseType to)
                => from == to || _functions.Types.ResolveCast(null, from, SqlType.ForStorage(new DatabaseTypeInfo(to)))
                    is { Context: SqlCastContext.Implicit };
            if (Category(left.Type) == 0 || Category(left.Type) != Category(right.Type)) { Refuse(); }
            if (CanConvert(left.Type, right.Type) && !CanConvert(right.Type, left.Type)) { common = right.Type; }
            if (!CanConvert(left.Type, common) || !CanConvert(right.Type, common)) { Refuse(); }
        }
        // PostgreSQL parse_coerce.c:1648-1678: a type or typmod mismatch clears the typmod.
        return left.Type == right.Type && left.MaxLength == right.MaxLength
            && left.Precision == right.Precision && left.Scale == right.Scale
                ? left : new DatabaseTypeInfo(common);

        void Refuse() => throw new SqlPlanningException(setOperation ? "COHSQLE003" : "COHSQLE015",
            setOperation ? string.Format(CultureInfo.CurrentCulture, ErrorMessages.SetColumnTypes, name, left.Type, right.Type)
                : $"USING column '{name}' requires a common type; found {left.Type} and {right.Type}.", select.Location);
    }

    /// <summary>Coerces the merged value without changing the physical input or qualified references.</summary>
    private SqlBoundExpression CoerceUsing(SqlBoundExpression value, DatabaseType source, DatabaseTypeInfo common)
    {
        if (source == common.Type) { return value; }
        var target = SqlType.ForStorage(common);
        return new SqlBoundCast(value, target.Name, target, _functions.Types.ResolveCasts(null, target),
            _functions.Database, _catalog.DefaultCollation);
    }

    /// <summary>Seeks into the added input; only the first INNER pair may reverse direction.</summary>
    private SqlJoinIndexPath? SelectJoinAccessPath(IReadOnlyList<SqlRelationBinding> bindings,
        IReadOnlyList<SqlCatalogColumn> columns, List<(int Left, int Right, Collation Collation)> equalities,
        int added, bool maySwap)
    {
        SqlJoinIndexPath? best = null;
        foreach (int inner in maySwap ? new[] { added, 0 } : new[] { added })
        {
            var innerBinding = bindings[inner];
            if (innerBinding.Source.Table is not { } table) { continue; }
            foreach (var index in _catalog.GetIndexes(table.ObjectId).OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase))
            {
                var outerOrdinals = new List<int>();
                var innerColumns = new List<SqlCatalogColumn>();
                foreach (string key in index.ColumnNames)
                {
                    int local = FindColumnOrdinal(table, key);
                    int joined = innerBinding.Offset + local;
                    int outer = -1;
                    foreach (var equality in equalities)
                    {
                        int candidate = equality.Left == joined ? equality.Right : equality.Right == joined ? equality.Left : -1;
                        bool inOuter = inner == 0 ? candidate >= bindings[1].Offset && candidate < columns.Count
                            : candidate >= 0 && candidate < innerBinding.Offset;
                        if (inOuter && CanSeekJoinEquality(columns[joined].Type.Type, columns[candidate].Type.Type) &&
                            (columns[joined].Type.Type is not (DatabaseType.String or DatabaseType.Character or DatabaseType.Json) ||
                                equality.Collation.IsIndexBacked && equality.Collation == (columns[joined].Collation ?? _catalog.DefaultCollation)))
                        {
                            outer = candidate;
                            break;
                        }
                    }
                    if (outer < 0) { break; }
                    outerOrdinals.Add(outer);
                    innerColumns.Add(columns[joined]);
                }
                if (outerOrdinals.Count > 0 && (best is null || outerOrdinals.Count > best.OuterOrdinals.Count ||
                    outerOrdinals.Count == best.OuterOrdinals.Count && (index.IsUnique && !best.Index.IsUnique ||
                    index.IsUnique == best.Index.IsUnique && StringComparer.OrdinalIgnoreCase.Compare(index.Name, best.Index.Name) < 0)))
                {
                    best = new SqlJoinIndexPath(inner, index, outerOrdinals, innerColumns);
                }
            }
        }
        return best;
    }

    /// <summary>Collects only mandatory column equalities, never OR alternatives.</summary>
    private static void CollectJoinEqualities(SqlExpression expression, SqlExpressionEvaluator evaluator,
        List<(int Left, int Right, Collation Collation)> equalities)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlLogicalExpression { Operator: SqlLogicalOperator.And } conjunction)
        {
            foreach (var term in conjunction.Operands)
            {
                CollectJoinEqualities(term, evaluator, equalities);
            }
        }
        else if (expression is SqlBinaryExpression { Operator: SqlBinaryOperator.Equal } binary
            && UnwrapCollation(binary.Left) is SqlColumnReferenceExpression left
            && UnwrapCollation(binary.Right) is SqlColumnReferenceExpression right)
        {
            if (evaluator.Bind(left) is SqlBoundColumn a && evaluator.Bind(right) is SqlBoundColumn b)
            {
                equalities.Add((a.Ordinal, b.Ordinal, evaluator.ResolveCollation(binary.Left, binary.Right)));
            }
        }
    }

    /// <summary>
    /// Requires key equality to match evaluator equality. Floating signed zeros
    /// share SQL equality but not key bytes, so floating keys must scan even with
    /// an index present. Timestamps qualify: their keys encode only the ticks
    /// (TIMESTAMP) or the instant (TIMESTAMPTZ), the identity the comparer uses.
    /// </summary>
    private static bool CanSeekJoinEquality(DatabaseType inner, DatabaseType outer)
    {
        static bool ExactNumeric(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16
            or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Decimal;

        return ExactNumeric(inner) && ExactNumeric(outer)
            || inner == outer && inner is DatabaseType.Boolean or DatabaseType.String or DatabaseType.Character or DatabaseType.Json
                or DatabaseType.Date or DatabaseType.Time or DatabaseType.DateTime or DatabaseType.DateTimeOffset
                or DatabaseType.TimeSpan or DatabaseType.Guid;
    }
}
