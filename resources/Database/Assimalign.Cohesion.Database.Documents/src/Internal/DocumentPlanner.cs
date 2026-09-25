using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentPlanner
{
    private readonly IDocumentCatalog _catalog;
    private readonly TransactionSnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentPlanner"/> class.
    /// </summary>
    /// <param name="catalog">The document catalog used to resolve collections and indexes.</param>
    /// <param name="snapshot">The transaction snapshot at which catalog metadata is read.</param>
    /// <param name="parameters">The OQL parameter values, or <see langword="null"/> when the statement supplies none.</param>
    public DocumentPlanner(IDocumentCatalog catalog, TransactionSnapshot snapshot, IReadOnlyDictionary<string, object?>? parameters)
    {
        _catalog = catalog;
        _snapshot = snapshot;
        _parameters = parameters;
    }

    internal DocumentStatementPlan Plan(OqlExpression expression) => expression switch
    {
        OqlSelectExpression select when DocumentSystemCollections.Find(select.Collection) is string name =>
            new DocumentSystemCollectionPlan(CreateLogicalPlan(select), name),
        OqlSelectExpression select => Plan(select),
        OqlCreateIndexExpression createIndex => PlanCreateIndex(createIndex),
        OqlDropIndexExpression dropIndex => PlanDropIndex(dropIndex),
        _ => throw new DatabaseException("The OQL statement is not supported by the document executor."),
    };

    internal DocumentPlan Plan(OqlSelectExpression query)
    {
        var logical = CreateLogicalPlan(query);
        var collection = ResolveCollection(query.Collection);
        return new DocumentPlan(logical, collection, ChooseAccess(query, _catalog.GetIndexes(collection.Id, _snapshot)));
    }

    private DocumentCreateIndexPlan PlanCreateIndex(OqlCreateIndexExpression create)
    {
        DocumentSystemCollections.EnsureReadOnly(create.Collection);
        if (string.IsNullOrWhiteSpace(create.IndexName))
        {
            throw new DatabaseException("CREATE INDEX requires an index name.");
        }

        string path = IndexPath(create.Path, alias: null)
            ?? throw new DatabaseException($"CREATE INDEX '{create.IndexName}' requires a representable document path.");
        return new DocumentCreateIndexPlan(ResolveCollection(create.Collection), create.IndexName, path);
    }

    private DocumentDropIndexPlan PlanDropIndex(OqlDropIndexExpression drop)
    {
        DocumentSystemCollections.EnsureReadOnly(drop.Collection);
        if (string.IsNullOrWhiteSpace(drop.IndexName))
        {
            throw new DatabaseException("DROP INDEX requires an index name.");
        }

        return new DocumentDropIndexPlan(ResolveCollection(drop.Collection), drop.IndexName);
    }

    private DocumentCollectionMetadata ResolveCollection(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DatabaseException("An OQL statement requires a collection name.");
        }

        return _catalog.FindCollection(name, _snapshot)
            ?? throw new DatabaseException($"Collection '{name}' does not exist.");
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
        var predicates = new List<(string Path, string? LegacyPath, string Operator, object Value)>();
        if (query.Predicate is not null) { Collect(query.Predicate); }
        DocumentIndexPath? best = null;
        bool bestEquality = false;
        foreach (var index in indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
        {
            var usable = predicates.Where(predicate =>
                string.Equals(predicate.Path, index.Path, StringComparison.Ordinal) ||
                predicate.LegacyPath is not null && string.Equals(predicate.LegacyPath, index.Path, StringComparison.Ordinal)).ToArray();
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
            var evaluator = new DocumentExpressionEvaluator(query.Alias, _parameters);
            var value = evaluator.Evaluate(constant, default);
            string? pathName = IndexPath(path, query.Alias);
            if (pathName is not null && value is bool or decimal or string)
            {
                predicates.Add((pathName, LegacyIndexPath(path, query.Alias), operation, value));
            }
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
                if (CanUseDottedSegment(name))
                {
                    if (builder.Length > 0) { builder.Append('.'); }
                    builder.Append(name);
                }
                else
                {
                    builder.Append("['").Append(name.Replace("'", "''", StringComparison.Ordinal)).Append("']");
                }
            }
            else { builder.Append('[').Append(segment.Index).Append(']'); }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool CanUseDottedSegment(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_')) { return false; }
        for (int i = 1; i < name.Length; i++)
        {
            if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_')) { return false; }
        }
        return true;
    }

    // The removed extension API accepted unquoted property segments whenever
    // they contained no structural path delimiter. Keep those persisted
    // definitions selectable after OQL DDL moves new paths to canonical
    // bracket quoting.
    private static string? LegacyIndexPath(OqlPathExpression path, string? alias)
    {
        int start = alias is not null && path.Segments.Count > 0 && path.Segments[0].Name == alias ? 1 : 0;
        var builder = new StringBuilder();
        for (int i = start; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            if (segment.Name is string name)
            {
                if (name.Length == 0 || name.IndexOfAny(['.', '[', ']']) >= 0) { return null; }
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
