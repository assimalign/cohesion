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
    /// <summary>Binds every step before execution, with ON visibility limited to its comma item.</summary>
    private IReadOnlyList<SqlRelationBinding> BindJoin(SqlSelectExpression select,
        out IReadOnlyList<SqlCatalogColumn> columns, out IReadOnlyList<SqlJoinStep> steps,
        out IReadOnlyList<int> outputOrdinals)
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
                    functions: _functions, cancellationToken: _cancellationToken);
                var equalities = new List<(int Left, int Right, Collation Collation)>();
                SqlBoundExpression? condition = null;
                if (join.Condition is { } on)
                {
                    if (ContainsAggregate(on))
                    {
                        throw new SqlPlanningException("COHSQLE017", "Aggregate functions are not allowed in JOIN ON.", on.Location);
                    }
                    ValidateExpression(on, scope, _subqueryTypes);
                    condition = scope.Bind(on);
                    CollectJoinEqualities(on, scope, equalities);
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
                        var leftColumn = allColumns[leftOrdinal];
                        var rightColumn = allColumns[rightOrdinal];
                        // USING's merged output retains the left column's type and value. Its
                        // implicit equality uses exactly the ordinary ON collation precedence.
                        var collation = SqlBoundCollation.Of(
                            new SqlCollationCandidate(leftColumn.Collation ?? _catalog.DefaultCollation, leftColumn.Collation is null ? 1 : 2),
                            new SqlCollationCandidate(rightColumn.Collation ?? _catalog.DefaultCollation, rightColumn.Collation is null ? 1 : 2),
                            _catalog.DefaultCollation);
                        predicates.Add(new SqlBoundBinary(SqlBinaryOperator.Equal, new SqlBoundColumn(leftOrdinal),
                            new SqlBoundColumn(rightOrdinal), collation));
                        equalities.Add((leftOrdinal, rightOrdinal, collation.Resolve(null)));
                        merged.Add(leftOrdinal);
                        right.HiddenColumns.Add(rightOrdinal - right.Offset);
                    }
                    condition = predicates.Count == 1 ? predicates[0] : new SqlBoundLogical(false, predicates.ToArray());
                    visible = merged.Concat(visible.Except(merged)).ToList();
                }
                if (!SqlLanguageProfile.SupportsJoin(join.JoinType) ||
                    (join.JoinType != SqlJoinType.Cross && condition is null) ||
                    (join.JoinType == SqlJoinType.Cross && condition is not null))
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

        void Add(SqlTableReference reference, bool nullable)
        {
            var source = ResolveRelation(reference);
            string name = reference.Alias ?? source.Name;
            if (bindings.Any(binding => string.Equals(name, binding.Reference.Alias ?? binding.Source.Name, StringComparison.OrdinalIgnoreCase) &&
                (reference.Alias is not null || binding.Reference.Alias is not null ||
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
            equalities.Add((evaluator.ResolveColumn(left), evaluator.ResolveColumn(right), evaluator.ResolveCollation(binary.Left, binary.Right)));
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
