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
        out IReadOnlyList<int> outputOrdinals, out SqlJoinTree? tree, bool metadataOnly = false)
        => BindJoinSources(select.From, select, first, out columns, out steps, out outputOrdinals, out tree, metadataOnly, select);

    private IReadOnlyList<SqlRelationBinding> BindJoinSources(IReadOnlyList<SqlFromItem> from, SqlQueryExpression statement,
        SqlRelationSource first, out IReadOnlyList<SqlCatalogColumn> columns, out IReadOnlyList<SqlJoinStep> steps,
        out IReadOnlyList<int> outputOrdinals, out SqlJoinTree? tree, bool metadataOnly = false, SqlSelectExpression? queryOwner = null,
        SqlTableReference? target = null, bool forceTree = false,
        Dictionary<SqlExpression, SqlExpressionEvaluator>? dmlScopes = null)
    {
        var bindings = new List<SqlRelationBinding>();
        var allColumns = new List<SqlCatalogColumn>();
        var allSteps = new List<SqlJoinStep>();
        var output = new List<int>();
        if (target is not null)
        {
            bindings.Add(new SqlRelationBinding(first, target, 0));
            allColumns.AddRange(first.Columns);
            output.AddRange(Enumerable.Range(0, first.Columns.Count));
        }
        bool treeNeeded = forceTree || from.Any(NeedsJoinTree);
        SqlJoinTree? whole = treeNeeded && target is not null ? new(0) : null;
        foreach (var item in from)
        {
            int start = bindings.Count;
            int stepStart = allSteps.Count;
            var part = BindItem(item);
            if (start > 0)
            {
                var comma = new SqlJoinStep(start, SqlJoinType.Cross, null, null);
                allSteps.Insert(stepStart, comma);
                if (treeNeeded) { whole = new(-1, whole, part.Tree, comma); }
            }
            else { whole = part.Tree; }
            output.AddRange(part.Visible);
        }
        columns = allColumns;
        steps = allSteps;
        outputOrdinals = output;
        tree = treeNeeded ? whole : null;
        return bindings;

        (List<int> Visible, SqlJoinTree? Tree) BindItem(SqlFromItem item)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            int start = bindings.Count;
            var left = AddReference(item.Table, false);
            var visible = left.Visible;
            var root = left.Tree;
            foreach (var join in item.Joins)
            {
                int rightIndex = bindings.Count;
                var rightPart = AddReference(join.Table, join.JoinType == SqlJoinType.LeftOuter);
                var scope = new SqlExpressionEvaluator(allColumns, _parameters, bindings.Skip(start).ToArray(),
                    defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots,
                    functions: _functions, cancellationToken: _cancellationToken, foldConstants: FoldQueryConstants,
                    outerScope: OuterScope, outerDependencies: OuterDependencies);
                if (metadataOnly && join.Condition is { } scopedCondition && (queryOwner is not null || dmlScopes is not null))
                {
                    var local = bindings.Skip(start).Select(binding =>
                    {
                        var copy = new SqlRelationBinding(binding.Source, binding.Reference, binding.Offset);
                        copy.HiddenColumns.UnionWith(binding.HiddenColumns);
                        if (binding.UsingColumns is { } merged) { copy.UsingColumns = new(merged); }
                        return copy;
                    }).ToArray();
                    var joinedScope = new SqlExpressionEvaluator(
                        allColumns.ToArray(), _parameters, local, defaultCollation: _catalog.DefaultCollation,
                        subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken, foldConstants: FoldQueryConstants);
                    if (queryOwner is not null) { (_queryScopes ??= new()).JoinScopes[(queryOwner, scopedCondition)] = joinedScope; }
                    dmlScopes?.Add(scopedCondition, joinedScope);
                }
                var equalities = new List<(int Left, int Right, Collation Collation)>();
                SqlBoundExpression? condition = null;
                if (join.Condition is { } on)
                {
                    if (ContainsAggregate(on))
                    {
                        if (!metadataOnly) { throw new SqlPlanningException("CS05017", ErrorMessages.SqlPlannerBindJoinSqlPlanning, on.Location); }
                    }
                    if (!metadataOnly)
                    {
                        ValidatePredicate(on, scope, "JOIN ON", _subqueryTypes);
                        condition = scope.BindPredicate(on);
                        if (ViewDefinitionBinding is not null) { CaptureViewJoin(on, bindings.Skip(start).ToArray(), scope); }
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
                            throw new SqlPlanningException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlPlannerBindJoinSqlPlanning2, name), statement.Location);
                        }
                        int leftOrdinal = FindUsing(visible, name, "left");
                        int rightOrdinal = FindUsing(rightPart.Visible, name, "right");
                        if (ViewDefinitionBinding is not null)
                        {
                            var leftSource = bindings.Last(binding => binding.Offset <= leftOrdinal);
                            RecordViewColumn(leftSource.Source, leftOrdinal - leftSource.Offset);
                            var rightSource = bindings.Last(binding => binding.Offset <= rightOrdinal);
                            RecordViewColumn(rightSource.Source, rightOrdinal - rightSource.Offset);
                        }
                        var previous = scope.MergedColumn(leftOrdinal);
                        var leftColumn = previous?.Column ?? allColumns[leftOrdinal];
                        var previousRight = scope.MergedColumn(rightOrdinal);
                        var rightColumn = previousRight?.Column ?? allColumns[rightOrdinal];
                        var common = UsingType(leftColumn.Type, rightColumn.Type, name, statement);
                        bool sameDomain = string.Equals(leftColumn.DomainName, rightColumn.DomainName, StringComparison.OrdinalIgnoreCase);
                        if (!sameDomain) { common = new DatabaseTypeInfo(common.Type); }
                        var leftValue = previous?.Value ?? new SqlBoundColumn(leftOrdinal);
                        var rightValue = previousRight?.Value ?? new SqlBoundColumn(rightOrdinal);
                        // PostgreSQL parse_clause.c:1676-1709,1718-1733: choose a common type,
                        // coerce inputs, prefer an unchanged INNER input, and preserve the outer side.
                        // RIGHT reads the coerced right value (parse_clause.c:1735-1738).
                        var value = join.JoinType == SqlJoinType.RightOuter
                            ? CoerceUsing(rightValue, rightColumn.Type, common)
                            : CoerceUsing(leftValue, leftColumn.Type, common);
                        if (join.JoinType == SqlJoinType.FullOuter)
                        {
                            // PostgreSQL parse_clause.c:1740-1756: a FULL merged column
                            // reads the first non-NULL input after both common-type coercions.
                            value = new SqlBoundCoalesce([value, CoerceUsing(rightValue, rightColumn.Type, common)], common.Type);
                        }
                        if (join.JoinType == SqlJoinType.Inner && !ReferenceEquals(value, leftValue))
                        {
                            // PostgreSQL parse_clause.c:1680-1700 also relabels a typmod
                            // mismatch; prefer the right input only if it needs no coercion.
                            var coercedRight = CoerceUsing(rightValue, rightColumn.Type, common);
                            if (ReferenceEquals(coercedRight, rightValue)) { value = rightValue; }
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
                            new SqlCatalogColumn(name, common,
                                join.JoinType == SqlJoinType.FullOuter ? leftColumn.IsNullable || rightColumn.IsNullable
                                    : join.JoinType == SqlJoinType.RightOuter ? rightColumn.IsNullable : leftColumn.IsNullable,
                                collation: join.JoinType == SqlJoinType.FullOuter
                                    ? leftColumn.Collation is not null || rightColumn.Collation is not null ? collation.Resolve(null) : null
                                    : join.JoinType == SqlJoinType.RightOuter ? rightColumn.Collation : leftColumn.Collation,
                                // PostgreSQL parse_coerce.c:1360-1387 preserves identical domains.
                                domainName: sameDomain ? leftColumn.DomainName : null), value);
                        merged.Add(leftOrdinal);
                        var hiddenOwner = bindings.Last(binding => binding.Offset <= rightOrdinal);
                        hiddenOwner.HiddenColumns.Add(rightOrdinal - hiddenOwner.Offset);
                    }
                    condition = predicates.Count == 1 ? predicates[0] : new SqlBoundLogical(false, predicates.ToArray());
                    visible = merged.Concat(visible.Except(merged)).ToList();
                }
                if (!metadataOnly && (!SqlLanguageProfile.SupportsJoin(join.JoinType) ||
                    (join.JoinType != SqlJoinType.Cross && condition is null) ||
                    (join.JoinType == SqlJoinType.Cross && condition is not null)))
                {
                    throw new SqlPlanningException("CS05013", ErrorMessages.SqlPlannerBindJoinSqlPlanning3, statement.Location);
                }
                if (join.JoinType is SqlJoinType.RightOuter or SqlJoinType.FullOuter)
                {
                    Nullify(start, rightIndex, join.UsingColumns);
                }
                if (join.JoinType == SqlJoinType.FullOuter) { Nullify(rightIndex, bindings.Count, []); }
                visible.AddRange(rightPart.Visible.Where(ordinal =>
                {
                    var owner = bindings.Last(binding => binding.Offset <= ordinal);
                    return !owner.HiddenColumns.Contains(ordinal - owner.Offset);
                }));
                var access = join.JoinType != SqlJoinType.FullOuter && bindings.Count == rightIndex + 1
                    ? SelectJoinAccessPath(bindings, allColumns, equalities, rightIndex,
                        !treeNeeded && rightIndex == 1 && start == 0 && join.JoinType == SqlJoinType.Inner) : null;
                if (join.JoinType == SqlJoinType.RightOuter)
                {
                    access = rightIndex == 1 && start == 0 && bindings.Count == 2
                        ? SelectJoinAccessPath(bindings, allColumns, equalities, rightIndex, false, 1) : null;
                }
                var step = new SqlJoinStep(rightIndex, join.JoinType, condition, access);
                if (!metadataOnly && join.JoinType == SqlJoinType.FullOuter)
                {
                    var keys = HashKeys(condition, bindings, allColumns, rightIndex, bindings.Count - 1, fullFrame: true);
                    if (keys is null)
                    {
                        throw new SqlPlanningException("CS05070", ErrorMessages.FullJoinCondition, join.Condition?.Location ?? statement.Location);
                    }
                    step = step with { Hash = keys };
                }
                allSteps.Add(step);
                if (treeNeeded)
                {
                    root = join.JoinType == SqlJoinType.RightOuter
                        ? new(-1, rightPart.Tree, root, step) : new(-1, root, rightPart.Tree, step);
                }
            }

            return (visible, root);
        }

        (List<int> Visible, SqlJoinTree? Tree) AddReference(SqlRelationReference reference, bool nullable)
        {
            if (reference is SqlJoinedTableReference group)
            {
                int start = bindings.Count;
                var part = BindItem(group.Item);
                if (nullable) { Nullify(start, bindings.Count, []); }
                return part;
            }
            int index = bindings.Count;
            Add(reference, nullable);
            return (Enumerable.Range(bindings[index].Offset, bindings[index].Source.Columns.Count).ToList(),
                treeNeeded ? new(index) : null);
        }

        void Nullify(int start, int end, IReadOnlyList<string> preservedMerged)
        {
            for (int i = start; i < end; i++)
            {
                var binding = bindings[i];
                for (int ordinal = binding.Offset; ordinal < binding.Offset + binding.Source.Columns.Count; ordinal++)
                {
                    allColumns[ordinal] = Nullable(allColumns[ordinal]);
                }
                if (binding.UsingColumns is not { } merged) { continue; }
                foreach (int ordinal in merged.Keys.ToArray())
                {
                    if (preservedMerged.Contains(allColumns[ordinal].Name, StringComparer.OrdinalIgnoreCase)) { continue; }
                    merged[ordinal] = merged[ordinal] with { Column = Nullable(merged[ordinal].Column) };
                }
            }
        }

        int FindUsing(IEnumerable<int> ordinals, string name, string side)
        {
            var found = ordinals.Where(i => string.Equals(allColumns[i].Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length != 1)
            {
                throw new SqlPlanningException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlPlannerBindJoinSqlPlanning4, name, (found.Length == 0 ? ErrorMessages.UsingColumnMissing : ErrorMessages.UsingColumnAmbiguous), side), statement.Location);
            }
            return found[0];
        }

        static SqlCatalogColumn Nullable(SqlCatalogColumn column) => column.IsNullable ? column
            : new SqlCatalogColumn(column.Name, column.Type, true, column.DefaultLiteral,
                column.Collation, column.DomainName, column.DefaultFunctionCalls);

        void Add(SqlRelationReference reference, bool nullable)
        {
            var source = bindings.Count == 0 ? first : ResolveRelation(reference, queryOwner);
            string name = reference.Alias ?? source.Name;
            if (bindings.Any(binding => string.Equals(name, binding.Reference.Alias ?? binding.Source.Name, StringComparison.OrdinalIgnoreCase) &&
                (reference.Alias is not null || binding.Reference.Alias is not null ||
                    source.Derived is { IsCommonTable: true } || binding.Source.Derived is { IsCommonTable: true } ||
                    string.Equals(source.Schema, binding.Source.Schema, StringComparison.OrdinalIgnoreCase))))
            {
                throw new SqlPlanningException("CS01019", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlPlannerBindJoinSqlPlanning5, name), statement.Location);
            }
            bindings.Add(new SqlRelationBinding(source, reference, allColumns.Count));
            allColumns.AddRange(source.Columns.Select(column => nullable && !column.IsNullable
                ? new SqlCatalogColumn(column.Name, column.Type, true, column.DefaultLiteral, column.Collation, column.DomainName, column.DefaultFunctionCalls)
                : column));
        }
    }

    private static IEnumerable<SqlRelationReference> SourceRelations(IReadOnlyList<SqlFromItem> sources)
    {
        foreach (var item in sources)
        {
            foreach (var relation in RelationLeaves(item.Table)) { yield return relation; }
            foreach (var join in item.Joins)
            { foreach (var relation in RelationLeaves(join.Table)) { yield return relation; } }
        }
    }

    private static IEnumerable<SqlRelationReference> RelationLeaves(SqlRelationReference reference)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (reference is not SqlJoinedTableReference group) { yield return reference; }
        else { foreach (var relation in SourceRelations([group.Item])) { yield return relation; } }
    }

    private static IEnumerable<SqlJoinClause> SourceJoins(IReadOnlyList<SqlFromItem> sources)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var item in sources)
        {
            if (item.Table is SqlJoinedTableReference first)
            { foreach (var join in SourceJoins([first.Item])) { yield return join; } }
            foreach (var join in item.Joins)
            {
                if (join.Table is SqlJoinedTableReference right)
                { foreach (var child in SourceJoins([right.Item])) { yield return child; } }
                yield return join;
            }
        }
    }

    private static bool NeedsJoinTree(SqlFromItem item) => item.Table is SqlJoinedTableReference ||
        item.Joins.Any(join => join.JoinType is SqlJoinType.RightOuter or SqlJoinType.FullOuter || join.Table is SqlJoinedTableReference);

    /// <summary>Selects the common USING type, retaining modifiers only when both inputs agree.</summary>
    private DatabaseTypeInfo UsingType(DatabaseTypeInfo left, DatabaseTypeInfo right, string name, SqlQueryExpression statement, bool setOperation = false)
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

        void Refuse() => throw new SqlPlanningException(setOperation ? "CS02003" : "CS01015",
            setOperation ? string.Format(CultureInfo.CurrentCulture, ErrorMessages.SetColumnTypes, name, left.Type, right.Type)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlPlannerUsingTypeSqlPlanning, name, left.Type, right.Type), statement.Location);
    }

    /// <summary>Coerces the merged value without changing the physical input or qualified references.</summary>
    private SqlBoundExpression CoerceUsing(SqlBoundExpression value, DatabaseTypeInfo source, DatabaseTypeInfo common)
    {
        if (source.Type == common.Type && source.MaxLength == common.MaxLength &&
            source.Precision == common.Precision && source.Scale == common.Scale) { return value; }
        var target = SqlType.ForStorage(common);
        return new SqlBoundCast(value, target.Name, target, _functions.Types.ResolveCasts(null, target),
            _functions.Database, _catalog.DefaultCollation, sourceType: source.Type);
    }

    /// <summary>Seeks into the added input; only the first INNER pair may reverse direction.</summary>
    private SqlJoinIndexPath? SelectJoinAccessPath(IReadOnlyList<SqlRelationBinding> bindings,
        IReadOnlyList<SqlCatalogColumn> columns, List<(int Left, int Right, Collation Collation)> equalities,
        int added, bool maySwap, int? firstBinding = null)
    {
        if (ViewDefinitionBinding is not null) { return null; }
        SqlJoinIndexPath? best = null;
        var alternatives = new List<SqlJoinIndexPath>();
        foreach (int inner in firstBinding is int driver ? new[] { driver == 1 ? 0 : added }
            : maySwap ? new[] { added, 0 } : new[] { added })
        {
            var innerBinding = bindings[inner];
            if (innerBinding.Source.Table is not { } table) { continue; }
            foreach (var index in ReadIndexes(table.ObjectId).OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase))
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
                if (outerOrdinals.Count == 0) { continue; }
                var path = new SqlJoinIndexPath(inner, index, outerOrdinals, innerColumns);
                alternatives.Add(path);
                if (best is null || outerOrdinals.Count > best.OuterOrdinals.Count ||
                    outerOrdinals.Count == best.OuterOrdinals.Count && (index.IsUnique && !best.Index.IsUnique ||
                    index.IsUnique == best.Index.IsUnique && StringComparer.OrdinalIgnoreCase.Compare(index.Name, best.Index.Name) < 0))
                {
                    best = path;
                }
            }
        }
        if (best is not null && alternatives.Count > 1) { best.Alternatives = alternatives; }
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
