using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentPlanner(IDocumentCatalog catalog, TransactionSnapshot snapshot, IReadOnlyDictionary<string, object?>? parameters)
{
    internal DocumentPlan Plan(OqlSelectExpression query)
    {
        var logical = CreateLogicalPlan(query);
        var collection = catalog.FindCollection(query.Collection, snapshot)
            ?? throw new DatabaseException($"Collection '{query.Collection}' does not exist.");
        return new DocumentPlan(logical, collection, ChooseAccess(query, catalog.GetIndexes(collection.Id, snapshot)));
    }

    internal static DocumentLogicalPlan CreateLogicalPlan(OqlSelectExpression query)
    {
        if (query.Projections.Count == 0) { throw new DatabaseException("OQL SELECT requires a projection."); }
        // An explicit projection alias is visible to ORDER BY. Other paths
        // continue to address source documents, including unprojected fields.
        var ordering = query.OrderBy.Select(order => order.Expression is OqlPathExpression { Segments.Count: 1 } path &&
            query.Projections.FirstOrDefault(projection => projection.Alias is not null && projection.Alias == path.Segments[0].Name) is { } projection
                ? new OqlOrdering(projection.Expression, order.Descending) : order).ToArray();
        query = new OqlSelectExpression(query.Collection, query.Alias, query.Projections, query.Predicate,
            query.GroupBy, query.Having, ordering, query.Location);
        bool grouped = query.GroupBy.Count > 0 || query.Projections.Any(projection => ContainsAggregate(projection.Expression)) ||
            (query.Having is not null && ContainsAggregate(query.Having)) || query.OrderBy.Any(order => ContainsAggregate(order.Expression));
        if (query.Predicate is not null)
        {
            Validate(query.Predicate, allowAggregate: false);
        }
        foreach (var key in query.GroupBy) { Validate(key, allowAggregate: false); }
        if (query.Having is not null && !grouped) { throw new DatabaseException("OQL HAVING requires grouping or an aggregate."); }
        var projections = new List<DocumentProjection>();
        foreach (var projection in query.Projections)
        {
            Validate(projection.Expression, allowAggregate: true, allowStar: !grouped);
            if (grouped) { ValidateGrouped(projection.Expression, query); }
            string name = projection.Alias ?? projection.Expression switch
            {
                OqlStarExpression => "document",
                OqlPathExpression path => path.Segments.LastOrDefault().Name ?? $"column{projections.Count + 1}",
                OqlCallExpression call => call.Name.ToLowerInvariant(),
                _ => $"column{projections.Count + 1}",
            };
            if (projections.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
            {
                throw new DatabaseException($"OQL projection name '{name}' occurs more than once; specify unique aliases.");
            }
            projections.Add(new DocumentProjection(name, projection.Expression));
        }
        if (query.Having is not null)
        {
            Validate(query.Having, allowAggregate: true);
            ValidateGrouped(query.Having, query);
        }
        foreach (var order in query.OrderBy)
        {
            Validate(order.Expression, allowAggregate: true);
            if (grouped) { ValidateGrouped(order.Expression, query); }
        }
        return new DocumentLogicalPlan(query, projections, grouped);
    }

    private DocumentAccessPath ChooseAccess(OqlSelectExpression query, IReadOnlyList<DocumentIndexMetadata> indexes)
    {
        var predicates = new List<(string Path, string Operator, object Value)>();
        if (query.Predicate is not null) { Collect(query.Predicate); }
        DocumentIndexPath? best = null;
        bool bestEquality = false;
        foreach (var index in indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
        {
            var usable = predicates.Where(predicate => string.Equals(predicate.Path, index.Path, StringComparison.Ordinal)).ToArray();
            var equality = usable.FirstOrDefault(predicate => predicate.Operator == "=");
            bool hasEquality = equality.Value is not null;
            DocumentSeekBound? lower = null;
            DocumentSeekBound? upper = null;
            if (hasEquality)
            {
                lower = upper = new DocumentSeekBound(equality.Value!, true);
            }
            else
            {
                foreach (var predicate in usable)
                {
                    if (predicate.Operator is ">" or ">=")
                    {
                        lower = Tighter(lower, new DocumentSeekBound(predicate.Value, predicate.Operator == ">="), isLower: true);
                    }
                    if (predicate.Operator is "<" or "<=")
                    {
                        upper = Tighter(upper, new DocumentSeekBound(predicate.Value, predicate.Operator == "<="), isLower: false);
                    }
                }
            }
            if ((lower is not null || upper is not null) && (best is null || hasEquality && !bestEquality))
            {
                best = new DocumentIndexPath(index, lower, upper);
                bestEquality = hasEquality;
            }
        }
        return (DocumentAccessPath?)best ?? new DocumentScanPath();

        void Collect(OqlExpression expression)
        {
            if (expression is not OqlBinaryExpression binary) { return; }
            if (binary.Operator == "AND") { Collect(binary.Left); Collect(binary.Right); return; }
            if (binary.Operator is not ("=" or ">" or ">=" or "<" or "<=")) { return; }
            OqlPathExpression? path = binary.Left as OqlPathExpression;
            OqlExpression constant = binary.Right;
            string operation = binary.Operator;
            if (path is null && binary.Right is OqlPathExpression rightPath)
            {
                path = rightPath;
                constant = binary.Left;
                operation = operation switch { ">" => "<", ">=" => "<=", "<" => ">", "<=" => ">=", _ => operation };
            }
            if (path is null || !IsConstant(constant)) { return; }
            var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters);
            var value = evaluator.Evaluate(constant, default);
            string? pathName = IndexPath(path, query.Alias);
            if (pathName is not null && value is bool or decimal or string) { predicates.Add((pathName, operation, value)); }
        }
    }

    private static DocumentSeekBound Tighter(DocumentSeekBound? current, DocumentSeekBound candidate, bool isLower)
    {
        if (current is null) { return candidate; }
        int comparison = DocumentExpressionEvaluator.Compare(candidate.Value, current.Value.Value);
        return comparison == 0 ? new DocumentSeekBound(candidate.Value, current.Value.Inclusive && candidate.Inclusive) :
            (isLower ? comparison > 0 : comparison < 0) ? candidate : current.Value;
    }

    internal static string? IndexPath(OqlPathExpression path, string? alias)
    {
        int start = alias is not null && path.Segments.Count > 0 && path.Segments[0].Name == alias ? 1 : 0;
        var builder = new StringBuilder();
        for (int i = start; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            if (segment.Name is string name)
            {
                // These names need a quoted path representation that index v1
                // deliberately does not support. A scan preserves correctness.
                if (name.IndexOfAny(['.', '[', ']']) >= 0) { return null; }
                if (builder.Length > 0) { builder.Append('.'); }
                builder.Append(name);
            }
            else { builder.Append('[').Append(segment.Index).Append(']'); }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool IsConstant(OqlExpression expression) => expression switch
    {
        OqlLiteralExpression or OqlParameterExpression => true,
        OqlUnaryExpression unary => IsConstant(unary.Operand),
        OqlBinaryExpression binary => IsConstant(binary.Left) && IsConstant(binary.Right),
        _ => false,
    };

    internal static bool ContainsAggregate(OqlExpression expression) => expression switch
    {
        OqlCallExpression => true,
        OqlBinaryExpression binary => ContainsAggregate(binary.Left) || ContainsAggregate(binary.Right),
        OqlUnaryExpression unary => ContainsAggregate(unary.Operand),
        _ => false,
    };

    private static void Validate(OqlExpression expression, bool allowAggregate, bool allowStar = false)
    {
        switch (expression)
        {
            case OqlLiteralExpression or OqlParameterExpression or OqlPathExpression:
                return;
            case OqlStarExpression when allowStar:
                return;
            case OqlBinaryExpression binary when binary.Operator is "AND" or "OR" or "=" or "!=" or "<>" or "<" or "<=" or ">" or ">=" or "+" or "-" or "*" or "/" or "%":
                Validate(binary.Left, allowAggregate);
                Validate(binary.Right, allowAggregate);
                return;
            case OqlUnaryExpression unary when unary.Operator is "+" or "-" or "NOT" or "IS NULL" or "IS NOT NULL":
                Validate(unary.Operand, allowAggregate);
                return;
            case OqlCallExpression call when allowAggregate && call.Name is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX":
                if (call.Arguments.Count != 1) { throw new DatabaseException($"OQL aggregate '{call.Name}' requires one argument."); }
                Validate(call.Arguments[0], allowAggregate: false, allowStar: call.Name == "COUNT");
                return;
            default:
                throw new DatabaseException("The expression is not valid in this OQL planning context.");
        }
    }

    private static void ValidateGrouped(OqlExpression expression, OqlSelectExpression query)
    {
        if (expression is OqlCallExpression or OqlLiteralExpression or OqlParameterExpression) { return; }
        if (query.GroupBy.Any(key => EquivalentExpression(key, expression, query.Alias))) { return; }
        if (expression is OqlBinaryExpression binary)
        {
            ValidateGrouped(binary.Left, query);
            ValidateGrouped(binary.Right, query);
            return;
        }
        if (expression is OqlUnaryExpression unary) { ValidateGrouped(unary.Operand, query); return; }
        throw new DatabaseException("An OQL grouped query may only read grouping expressions outside aggregate calls.");
    }

    private static bool EquivalentExpression(OqlExpression left, OqlExpression right, string? alias) => (left, right) switch
    {
        (OqlPathExpression a, OqlPathExpression b) => PathSegments(a, alias).SequenceEqual(PathSegments(b, alias)),
        (OqlLiteralExpression a, OqlLiteralExpression b) => DocumentExpressionEvaluator.Compare(a.Value, b.Value) == 0,
        (OqlParameterExpression a, OqlParameterExpression b) => a.Name == b.Name,
        (OqlBinaryExpression a, OqlBinaryExpression b) => a.Operator == b.Operator &&
            EquivalentExpression(a.Left, b.Left, alias) && EquivalentExpression(a.Right, b.Right, alias),
        (OqlUnaryExpression a, OqlUnaryExpression b) => a.Operator == b.Operator && EquivalentExpression(a.Operand, b.Operand, alias),
        _ => false,
    };

    private static IEnumerable<OqlPathSegment> PathSegments(OqlPathExpression path, string? alias)
        => path.Segments.Skip(alias is not null && path.Segments.Count > 0 && path.Segments[0].Name == alias ? 1 : 0);
}
