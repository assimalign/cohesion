using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Language.Internal;

internal static class OqlMergeRenderer
{
    internal static bool IsWithinStatementDepth(OqlMergeExpression merge)
    {
        var pending = new Stack<(OqlExpression Node, int Depth, int ParseDepth)>();
        pending.Push((merge, 1, 0));
        while (pending.TryPop(out var item))
        {
            if (item.Depth > 128 || item.ParseDepth > 128) { return false; }
            int childOrdinal = 0;
            foreach (var child in Children(item.Node))
            {
                // Fully grouped rendering preserves every precedence boundary. Account
                // for the parser's operand recursion as well as each emitted grouping.
                int extra = item.Node switch
                {
                    OqlMergeExpression when child is OqlReturningExpression => 0,
                    OqlSubqueryExpression => 0,
                    OqlSelectExpression select when childOrdinal - (select.Source is null ? 0 : 1) is int bindingIndex &&
                        bindingIndex >= 0 && bindingIndex < select.Bindings.Count && select.Bindings[bindingIndex] is OqlJoinExpression => 2,
                    OqlBinaryExpression => childOrdinal == 0 ? 1 : 2,
                    OqlUnaryExpression unary => unary.Operator is "+" or "-" or "NOT" or "EXISTS" ? 2 : 1,
                    OqlInExpression or OqlBetweenExpression or OqlLikeExpression => childOrdinal == 0 ? 1 : 2,
                    _ => 1,
                };
                pending.Push((child, item.Depth + 1, item.ParseDepth + extra));
                childOrdinal++;
            }
        }
        return true;
    }

    // Include query and action expressions in the same path bound. In particular a long
    // left-associative expression must not bypass the recursive parser's nesting counter.
    internal static IEnumerable<OqlExpression> Children(OqlExpression node)
    {
        switch (node)
        {
            case OqlMergeExpression merge:
                if (merge.Source.Expression is not null) { yield return merge.Source.Expression; }
                yield return merge.On;
                if (merge.Update is { } update)
                {
                    foreach (var assignment in update.Assignments) { yield return assignment.Path; yield return assignment.Value; }
                    foreach (var path in update.Unset) { yield return path; }
                    if (update.Predicate is not null) { yield return update.Predicate; }
                }
                if (merge.Delete?.Predicate is { } deleted) { yield return deleted; }
                if (merge.Insert is { } inserted)
                {
                    if (inserted.Key is not null) { yield return inserted.Key; }
                    yield return inserted.Value;
                    if (inserted.Predicate is not null) { yield return inserted.Predicate; }
                }
                if (merge.Returning is not null) { yield return merge.Returning; }
                break;
            case OqlReturningExpression returning: foreach (var projection in returning.Projections) { yield return projection.Expression; } break;
            case OqlSubqueryExpression subquery: yield return subquery.Query; break;
            case OqlSetOperationExpression compound: yield return compound.Left; yield return compound.Right; break;
            case OqlSelectExpression select:
                if (select.Source is not null) { yield return select.Source; }
                foreach (var binding in select.Bindings) { yield return binding.Expression; }
                foreach (var projection in select.Projections) { yield return projection.Expression; }
                if (select.Predicate is not null) { yield return select.Predicate; }
                foreach (var group in select.GroupBy) { yield return group; }
                if (select.Having is not null) { yield return select.Having; }
                break;
            case OqlUnaryExpression unary: yield return unary.Operand; break;
            case OqlBinaryExpression binary: yield return binary.Left; yield return binary.Right; break;
            case OqlMetaExpression meta: foreach (var argument in meta.Arguments) { yield return argument; } break;
            case OqlCallExpression call: foreach (var argument in call.Arguments) { yield return argument; } break;
            case OqlArrayExpression array: foreach (var element in array.Elements) { yield return element; } break;
            case OqlObjectExpression obj: foreach (var member in obj.Members) { yield return member.Value; } break;
            case OqlComprehensionExpression comprehension:
                yield return comprehension.Collection; yield return comprehension.Projection;
                if (comprehension.Predicate is not null) { yield return comprehension.Predicate; }
                break;
            case OqlQuantifiedExpression quantified: yield return quantified.Collection; yield return quantified.Predicate; break;
            case OqlInExpression membership:
                yield return membership.Operand;
                if (membership.Array is not null) { yield return membership.Array; }
                foreach (var value in membership.Values) { yield return value; }
                break;
            case OqlBetweenExpression between: yield return between.Operand; yield return between.Lower; yield return between.Upper; break;
            case OqlLikeExpression like:
                yield return like.Operand; yield return like.Pattern;
                if (like.Escape is not null) { yield return like.Escape; }
                break;
            case OqlCaseExpression conditional:
                if (conditional.Operand is not null) { yield return conditional.Operand; }
                foreach (var branch in conditional.Branches) { yield return branch.Key; yield return branch.Value; }
                if (conditional.Otherwise is not null) { yield return conditional.Otherwise; }
                break;
        }
        if (node is OqlQueryExpression query)
        {
            foreach (var order in query.OrderBy) { yield return order.Expression; }
            if (query.Limit is not null) { yield return query.Limit; }
            if (query.Offset is not null) { yield return query.Offset; }
        }
    }

    internal static string Render(OqlMergeExpression merge)
    {
        if (!IsWithinStatementDepth(merge)) { throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid); }
        string result = "MERGE INTO " + Collection(merge.Collection) + Alias(merge.Alias) + " USING " +
            (merge.Source.Expression is null ? Collection(merge.Source.Collection) : Expression(merge.Source.Expression)) + Alias(merge.Source.Alias) +
            " ON " + (merge.IsLookup ? merge.IsPrimary ? "PRIMARY KEY " : "KEY " : "") + Expression(merge.On);
        if (merge.Update is { } update)
        {
            result += " WHEN MATCHED THEN UPDATE";
            if (update.Assignments.Count > 0)
            { result += " SET " + string.Join(", ", update.Assignments.Select(a => Expression(a.Path) + " = " + Expression(a.Value))); }
            if (update.Unset.Count > 0) { result += " UNSET " + string.Join(", ", update.Unset.Select(p => Expression(p))); }
            result += Where(update.Predicate);
        }
        if (merge.Delete is { } delete) { result += " WHEN MATCHED THEN DELETE" + Where(delete.Predicate); }
        if (merge.Insert is { } insert)
        {
            result += " WHEN NOT MATCHED THEN INSERT " + (merge.IsLookup ? Expression(insert.Value) :
                "(KEY " + Expression(insert.Key!) + ", VALUE " + Expression(insert.Value) + ")") + Where(insert.Predicate);
        }
        if (merge.Returning is { } returning)
        { result += " RETURNING " + (returning.IsValue ? "RAW " : "") + Projections(returning.Projections, 0); }
        return result;
    }

    private static string Name(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Collection(string value) => value.StartsWith("COHESION_SCHEMA.", StringComparison.OrdinalIgnoreCase)
        ? value[.."COHESION_SCHEMA".Length] + "." + Name(value["COHESION_SCHEMA.".Length..]) : Name(value);
    private static string Alias(string? value) => value is null ? "" : " AS " + Name(value);
    private static string Where(OqlExpression? predicate) => predicate is null ? "" : " WHERE " + Expression(predicate);
    private static string Projections(IReadOnlyList<OqlProjection> projections, int depth)
        => string.Join(", ", projections.Select(p => Expression(p.Expression, depth + 1) + Alias(p.Alias)));

    private static string Expression(OqlExpression node, int depth = 0)
    {
        if (depth > 256 || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        { throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid); }
        string E(OqlExpression value) => Expression(value, depth + 1);
        string List(IEnumerable<OqlExpression> values) => string.Join(", ", values.Select(E));
        switch (node)
        {
            case OqlLiteralExpression literal: return Literal(literal.Value);
            case OqlMissingExpression: return "MISSING";
            case OqlParameterExpression parameter: return "$" + parameter.Name.TrimStart('@', ':', '$');
            case OqlStarExpression: return "*";
            case OqlPathExpression path:
                if (path.Segments.Count == 0 || string.IsNullOrEmpty(path.Segments[0].Name) || path.Segments[0].Index is not null)
                { throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid); }
                return string.Concat(path.Segments.Select((p, i) => p.Index is int index ? "[" + index.ToString(CultureInfo.InvariantCulture) + "]" :
                    i > 0 && p.Name == "" ? "['']" : (i == 0 ? "" : ".") + Name(p.Name!)));
            case OqlMetaExpression meta: return "META(" + List(meta.Arguments) + ")" + (meta.Member is null ? "" : "." + Name(meta.Member));
            case OqlBinaryExpression binary:
                if (binary.Operator is not ("OR" or "AND" or "=" or "!=" or "<>" or "<" or "<=" or ">" or ">=" or
                    "IS DISTINCT FROM" or "IS NOT DISTINCT FROM" or "+" or "-" or "*" or "/" or "%"))
                { throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid); }
                return "(" + E(binary.Left) + " " + binary.Operator + " " + E(binary.Right) + ")";
            case OqlUnaryExpression unary:
                if (unary.Operator == ".*") { return E(unary.Operand) + ".*"; }
                if (unary.Operator is "IS NULL" or "IS NOT NULL" or "IS MISSING" or "IS NOT MISSING" or "IS VALUED" or "IS NOT VALUED")
                { return "(" + E(unary.Operand) + " " + unary.Operator + ")"; }
                if (unary.Operator is not ("+" or "-" or "NOT" or "EXISTS")) { throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid); }
                return "(" + unary.Operator + " " + E(unary.Operand) + ")";
            case OqlCallExpression call: return Name(call.Name) + "(" + (call.Quantifier == OqlAggregateQuantifier.None ? "" : call.Quantifier.ToString().ToUpperInvariant() + " ") + List(call.Arguments) + ")";
            case OqlArrayExpression array: return "[" + List(array.Elements) + "]";
            case OqlObjectExpression obj: return "{" + string.Join(", ", obj.Members.Select(m => Literal(m.Key) + ": " + E(m.Value))) + "}";
            case OqlSubqueryExpression subquery: return "(" + Query(subquery.Query, depth + 1) + ")";
            case OqlComprehensionExpression comprehension:
                return (comprehension.IsFirst ? "FIRST " : "ARRAY ") + E(comprehension.Projection) + " FOR " + Name(comprehension.Variable) +
                    " IN " + E(comprehension.Collection) + (comprehension.Predicate is null ? "" : " WHEN " + E(comprehension.Predicate)) + " END";
            case OqlQuantifiedExpression quantified:
                return (quantified.IsEvery ? "EVERY " : "ANY ") + Name(quantified.Variable) + " IN " + E(quantified.Collection) + " SATISFIES " + E(quantified.Predicate) + " END";
            case OqlInExpression membership:
                return "(" + E(membership.Operand) + (membership.Negated ? " NOT IN " : " IN ") +
                    (membership.Array is null ? "(" + List(membership.Values) + ")" : E(membership.Array)) + ")";
            case OqlBetweenExpression between:
                return "(" + E(between.Operand) + (between.Negated ? " NOT BETWEEN " : " BETWEEN ") + E(between.Lower) + " AND " + E(between.Upper) + ")";
            case OqlLikeExpression like:
                return "(" + E(like.Operand) + (like.Negated ? " NOT LIKE " : " LIKE ") + E(like.Pattern) + (like.Escape is null ? "" : " ESCAPE " + E(like.Escape)) + ")";
            case OqlCaseExpression conditional:
                return "CASE" + (conditional.Operand is null ? "" : " " + E(conditional.Operand)) +
                    string.Concat(conditional.Branches.Select(b => " WHEN " + E(b.Key) + " THEN " + E(b.Value))) +
                    (conditional.Otherwise is null ? "" : " ELSE " + E(conditional.Otherwise)) + " END";
            default: throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid);
        }
    }

    private static string Query(OqlQueryExpression query, int depth)
    {
        if (depth > 256 || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        { throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid); }
        string E(OqlExpression value) => Expression(value, depth + 1);
        string text;
        if (query is OqlSetOperationExpression compound)
        {
            // Equal, left-associative set precedence permits an unpaged left spine to
            // stay flat. This also preserves IN ((SELECT ... UNION ...)) as a value
            // list instead of introducing a parenthesized first operand (query mode).
            string left = Query(compound.Left, depth + 1);
            if (compound.Left.OrderBy.Count > 0 || compound.Left.Limit is not null || compound.Left.Offset is not null)
            { left = "(" + left + ")"; }
            text = left + " " + compound.Operation.ToString().ToUpperInvariant() +
                (compound.IsAll ? " ALL " : " ") + "(" + Query(compound.Right, depth + 1) + ")";
        }
        else
        {
            var select = (OqlSelectExpression)query;
            text = "SELECT " + (select.IsDistinct ? "DISTINCT " : "") + (select.IsValue ? "RAW " : "") + Projections(select.Projections, depth) +
                " FROM " + (select.Source is null ? Collection(select.Collection) : E(select.Source)) +
                Alias(select.IsAliasImplicit ? null : select.Alias);
            foreach (var binding in select.Bindings)
            {
                if (binding is OqlJoinExpression join)
                { text += (join.IsLeft ? " LEFT " : " ") + (join.IsNest ? "NEST " : "JOIN ") + Collection(join.Collection) + Alias(join.Alias) +
                    " ON META(" + Name(join.Variable) + ").id = " + E(join.Expression); }
                else
                {
                    var unnest = (OqlUnnestExpression)binding;
                    text += (unnest.IsLeft ? " LEFT UNNEST " : " UNNEST ") + E(unnest.Expression) + Alias(unnest.Alias);
                }
            }
            if (select.Predicate is not null) { text += " WHERE " + E(select.Predicate); }
            if (select.GroupBy.Count > 0)
            { text += " GROUP BY " + string.Join(", ", select.GroupBy.Select((g, i) => E(g) + Alias(select.GroupAliases.Count > i ? select.GroupAliases[i] : null))); }
            if (select.GroupAs is not null) { text += " GROUP AS " + Name(select.GroupAs); }
            if (select.Having is not null) { text += " HAVING " + E(select.Having); }
        }
        if (query.OrderBy.Count > 0)
        { text += " ORDER BY " + string.Join(", ", query.OrderBy.Select(o => E(o.Expression) + (o.Descending ? " DESC" : " ASC") +
            (o.NullPlacement == OqlNullPlacement.Default ? "" : o.NullPlacement == OqlNullPlacement.First ? " NULLS FIRST" : " NULLS LAST"))); }
        if (query.Limit is not null) { text += " LIMIT " + Count(query.Limit); }
        if (query.Offset is not null) { text += " OFFSET " + Count(query.Offset); }
        return text;

        string Count(OqlExpression value) => value is OqlUnaryExpression { Operator: "+" or "-" } sign
            ? sign.Operator + " " + E(sign.Operand) : E(value);
    }

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        bool flag => flag ? "TRUE" : "FALSE",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        JsonElement json => Json(json, 0),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid),
    };

    private static string Json(JsonElement value, int depth)
    {
        if (depth > 256 || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        { throw new InvalidOperationException(ErrorMessages.MergeRenderInvalid); }
        return value.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(", ", value.EnumerateObject().Select(p => Literal(p.Name) + ": " + Json(p.Value, depth + 1))) + "}",
            JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(element => Json(element, depth + 1))) + "]",
            JsonValueKind.String => Literal(value.GetString()),
            JsonValueKind.Null => "NULL",
            JsonValueKind.True => "TRUE",
            JsonValueKind.False => "FALSE",
            _ => value.GetRawText(),
        };
    }
}
