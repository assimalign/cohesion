using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private IReadOnlyList<SqlCommonTableExpression> OrderRecursiveDefinitions(
        IReadOnlyList<SqlCommonTableExpression> definitions, out HashSet<SqlCommonTableExpression> recursive)
    {
        // PostgreSQL src/backend/parser/parse_cte.c:648-725,863-906: ignore self
        // edges for sorting, but remember them for recursive form validation.
        recursive = new(ReferenceEqualityComparer.Instance);
        var names = definitions.Select((definition, index) => (definition.Name, index))
            .ToDictionary(item => item.Name, item => item.index, StringComparer.OrdinalIgnoreCase);
        var dependencies = new HashSet<int>[definitions.Count];
        var dependents = new List<int>[definitions.Count];
        for (int index = 0; index < definitions.Count; index++) { dependents[index] = []; }
        var ready = new SortedSet<int>();
        for (int index = 0; index < definitions.Count; index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var edges = dependencies[index] = [];
            foreach (var reference in CommonTableReferences(definitions[index].Body))
            {
                if (reference.Table.SchemaName is not null || !names.TryGetValue(reference.Table.TableName, out int target)) { continue; }
                if (target == index) { recursive.Add(definitions[index]); }
                else if (edges.Add(target)) { dependents[target].Add(index); }
            }
            if (edges.Count == 0) { ready.Add(index); }
        }
        var ordered = new List<SqlCommonTableExpression>(definitions.Count);
        while (ready.Count > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int next = ready.Min;
            ready.Remove(next);
            ordered.Add(definitions[next]);
            foreach (int dependent in dependents[next])
            {
                dependencies[dependent].Remove(next);
                if (dependencies[dependent].Count == 0) { ready.Add(dependent); }
            }
        }
        if (ordered.Count != definitions.Count)
        {
            throw new SqlUnsupportedQueryException(ErrorMessages.CteMutualRecursion,
                location: definitions.First(definition => !ordered.Contains(definition)).Location);
        }
        foreach (var definition in ordered.Where(recursive.Contains)) { ValidateRecursiveDefinition(definition); }
        return ordered;
    }

    private void ValidateRecursiveDefinition(SqlCommonTableExpression definition)
    {
        // PostgreSQL parse_cte.c:938-1019 checks the UNION and its decorations before
        // looking for the one recursive reference in the right operand.
        if (definition.Body is not SqlSetOperationExpression { Operator: SqlSetOperator.Union } union)
        { throw RecursiveError("CS01054", ErrorMessages.CteRecursiveForm, definition); }
        if (union.OrderBy.Count > 0)
        { throw new SqlUnsupportedQueryException(ErrorMessages.CteRecursiveOrder, location: union.OrderBy[0].Expression.Location); }
        if (union.Offset is not null)
        { throw new SqlUnsupportedQueryException(ErrorMessages.CteRecursiveOffset, location: union.Offset.Location); }
        if (union.Limit is not null)
        { throw new SqlUnsupportedQueryException(ErrorMessages.CteRecursiveLimit, location: union.Limit.Location); }
        Check(union.Left, "CS01055");
        int count = Check(union.Right, null);
        if (count != 1) { throw RecursiveError("CS01060", ErrorMessages.CteRecursiveReferenceCount, definition); }

        int Check(SqlQueryBodyExpression body, string? restriction)
        {
            int references = 0;
            foreach (var reference in CommonTableReferences(body, restriction))
            {
                if (reference.Table.SchemaName is not null || !string.Equals(reference.Table.TableName, definition.Name, StringComparison.OrdinalIgnoreCase)) { continue; }
                string? code = reference.Restriction;
                if (code is not null)
                {
                    string message = code switch
                    {
                        "CS01055" => ErrorMessages.CteRecursiveAnchor,
                        "CS01056" => ErrorMessages.CteRecursiveSubquery,
                        "CS01057" => ErrorMessages.CteRecursiveOuterJoin,
                        "CS01058" => ErrorMessages.CteRecursiveIntersect,
                        _ => ErrorMessages.CteRecursiveExcept,
                    };
                    throw RecursiveError(code, message, definition, reference.Select);
                }
                if (++references > 1) { throw RecursiveError("CS01060", ErrorMessages.CteRecursiveReferenceCount, definition, reference.Select); }
                // parse_agg.c:1327-1334 refuses aggregates at the level containing
                // the self-reference; independent subquery aggregates remain legal.
                if (reference.Select is { } select && SqlViewQuery.Expressions(select).Any(ContainsAggregate))
                { throw RecursiveError("CS01061", ErrorMessages.CteRecursiveAggregate, definition, select); }
            }
            return references;
        }
    }

    private SqlDerivedRelation BindRecursiveDefinition(SqlCommonTableExpression definition)
    {
        var union = (SqlSetOperationExpression)definition.Body;
        var state = _commonTableState!;
        var anchor = PlanQuery(union.Left);
        var output = QueryOutput(anchor);
        if (definition.Columns.Count > 0 && definition.Columns.Count != output.Projections.Count)
        { throw RecursiveError("CS02065", ErrorMessages.CteRecursiveColumnCount, definition); }
        var columns = BindDerivedOutput(anchor, definition.Name, definition.Columns, union.Left).Columns
            .Select(column => new SqlCatalogColumn(column.Name,
                column.Type.Type == DatabaseType.Null ? new DatabaseTypeInfo(DatabaseType.String) : column.Type,
                true, collation: column.Collation, domainName: column.DomainName)).ToArray();
        // parse_cte.c:580-632 resolves unknown anchor output to text before publishing
        // the self-reference. This binding-only relation never reaches an executor.
        var relation = new SqlDerivedRelation(definition.Name, anchor, columns) { IsCommonTable = true, IsRecursiveReference = true };
        state.Relations![definition.Name] = relation;
        var recursive = PlanQuery(union.Right);
        var second = QueryOutput(recursive);
        if (columns.Length != second.Projections.Count)
        { throw RecursiveError("CS02065", ErrorMessages.CteRecursiveColumnCount, definition); }
        for (int index = 0; index < columns.Length; index++)
        {
            var first = columns[index];
            var projection = second.Projections[index];
            var next = SetColumn(projection, second.Columns);
            DatabaseTypeInfo common;
            try
            {
                common = next.Unknown ? new DatabaseTypeInfo(first.Type.Type)
                    : UsingType(first.Type, next.Type, (index + 1).ToString(CultureInfo.InvariantCulture), union, setOperation: true);
            }
            catch (SqlPlanningException exception) when (exception.Diagnostics[0].Code == "CS02003")
            { throw TypeError(); }
            if (common.Type != first.Type.Type || common.MaxLength != first.Type.MaxLength || common.Precision != first.Type.Precision ||
                common.Scale != first.Type.Scale || first.DomainName is not null &&
                (next.Unknown || !string.Equals(first.DomainName, next.Domain, StringComparison.OrdinalIgnoreCase)))
            { throw TypeError(common); }
            _ = CoerceSetValue(index, next.Type.Type, common, next.Unknown ? projection.Expression : null);
            if (first.Type.Type is DatabaseType.String or DatabaseType.Character)
            {
                var a = output.Projections[index].Metadata?.Collation.Resolve(null) ?? default;
                var b = projection.Metadata?.Collation.Resolve(null) ?? default;
                var anchorCollation = first.Collation ?? _catalog.DefaultCollation;
                var overall = SqlCollationCandidate.Choose(a, b) ?? _catalog.DefaultCollation;
                // parse_collate.c:807-860: a non-default implicit collation beats
                // default; distinct non-default peers conflict, as do explicit peers.
                bool conflict = a.Priority > 0 && a.Priority == b.Priority && a.Collation != b.Collation;
                if (conflict && a.Priority < 3)
                {
                    if (a.Collation is null || a.Collation == _catalog.DefaultCollation)
                    { overall = b.Collation ?? _catalog.DefaultCollation; conflict = false; }
                    else if (b.Collation is null || b.Collation == _catalog.DefaultCollation)
                    { overall = a.Collation; conflict = false; }
                }
                if (overall != anchorCollation || conflict)
                {
                    throw new SqlPlanningException("CS02067", string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.CteRecursiveCollation, definition.Name, index + 1, anchorCollation.Name,
                        (conflict ? b.Collation ?? _catalog.DefaultCollation : overall).Name), projection.Expression?.Location ?? union.Right.Location);
                }
            }

            SqlPlanningException TypeError(DatabaseTypeInfo? overall = null) => new("CS02066", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.CteRecursiveType, definition.Name, index + 1, TypeName(first.Type, first.DomainName),
                TypeName(overall ?? next.Type, overall is null ? next.Domain : null)),
                projection.Expression?.Location ?? union.Right.Location);
        }
        state.Dependencies!.Remove(relation);
        // Later WITH entries and the main query read the completed CTE; their
        // aggregates are not aggregates of this definition's recursive term.
        return relation with { IsRecursiveReference = false };

        static string TypeName(DatabaseTypeInfo type, string? domain)
            => domain ?? (type.Type == DatabaseType.Null ? "NULL" : SqlType.ForStorage(type).Name);
    }

    private static SqlPlanningException RecursiveError(string code, string message, SqlCommonTableExpression definition, SqlSelectExpression? select = null)
        => new(code, string.Format(CultureInfo.CurrentCulture, message, definition.Name), select?.Location ?? definition.Location);

    private IEnumerable<(SqlTableReference Table, string? Restriction, SqlSelectExpression? Select)> CommonTableReferences(
        SqlQueryBodyExpression query, string? restriction = null)
    {
        // Explicit stack keeps long set chains and dependency lists off the CLR stack.
        var pending = new Stack<(object Node, string? Restriction, SqlSelectExpression? Select)>();
        pending.Push((query, restriction, null));
        while (pending.TryPop(out var item))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (item.Node)
            {
                case SqlTableReference table:
                    yield return (table, item.Restriction, item.Select);
                    break;
                case SqlDerivedTableReference derived:
                    pending.Push((derived.Query, item.Restriction, null));
                    break;
                case SqlJoinedTableReference group:
                    Sources(group.Item, item.Restriction, item.Select);
                    break;
                case SqlQueryBodyExpression body:
                    if (body is SqlSetOperationExpression set)
                    {
                        string? left = item.Restriction ?? (set.IsAll ? set.Operator switch
                        {
                            SqlSetOperator.Intersect => "CS01058",
                            SqlSetOperator.Except => "CS01059",
                            _ => null,
                        } : null);
                        string? right = item.Restriction ?? (set.Operator == SqlSetOperator.Except ? "CS01059" : left);
                        pending.Push((set.Right, right, null));
                        pending.Push((set.Left, left, null));
                    }
                    if (body is SqlSelectExpression select)
                    {
                        foreach (var from in select.From) { Sources(from, item.Restriction, select); }
                    }
                    foreach (var expression in SqlViewQuery.Expressions(body)) { pending.Push((expression, item.Restriction, body as SqlSelectExpression)); }
                    break;
                case SqlExpression expression:
                    SqlQueryBodyExpression? child = expression switch
                    {
                        SqlSubqueryExpression scalar => scalar.Select,
                        SqlExistsExpression exists => exists.Subquery,
                        SqlInExpression member => member.Subquery,
                        SqlQuantifiedComparisonExpression quantified => quantified.Subquery,
                        _ => null,
                    };
                    if (child is not null) { pending.Push((child, "CS01056", null)); }
                    foreach (var operand in Children(expression)) { pending.Push((operand, item.Restriction, item.Select)); }
                    break;
            }
        }

        void Sources(SqlFromItem from, string? context, SqlSelectExpression? select)
        {
            // A RIGHT/FULL join null-extends the complete prefix, including grouped
            // relations. LEFT/FULL null-extends its right input (parse_cte.c:1142-1175).
            string? prefix = context;
            for (int index = from.Joins.Count - 1; index >= 0; index--)
            {
                var join = from.Joins[index];
                string? right = prefix ?? (join.JoinType is SqlJoinType.LeftOuter or SqlJoinType.FullOuter ? "CS01057" : null);
                pending.Push((join.Table, right, select));
                if (join.Condition is { } condition) { pending.Push((condition, prefix, select)); }
                if (join.JoinType is SqlJoinType.RightOuter or SqlJoinType.FullOuter) { prefix ??= "CS01057"; }
            }
            pending.Push((from.Table, prefix, select));
        }
    }
}
