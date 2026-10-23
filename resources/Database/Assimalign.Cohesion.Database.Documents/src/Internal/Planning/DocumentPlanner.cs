using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentPlanner")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanner.DebugView))]
internal sealed partial class DocumentPlanner
{
    private readonly DocumentCatalog _catalog;
    private readonly CancellationToken _cancellationToken;
    private readonly TransactionSnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private readonly bool _estimateSinglePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentPlanner"/> class.
    /// </summary>
    /// <param name="catalog">The document catalog used to resolve collections and indexes.</param>
    /// <param name="snapshot">The transaction snapshot at which catalog metadata is read.</param>
    /// <param name="parameters">The OQL parameter values, or <see langword="null"/> when the statement supplies none.</param>
    /// <param name="cancellationToken">The statement cancellation token, including constant evaluation.</param>
    /// <param name="estimateSinglePath">Whether a description also needs estimates for an uncontested input.</param>
    public DocumentPlanner(DocumentCatalog catalog, TransactionSnapshot snapshot, IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken = default, bool estimateSinglePath = false)
    {
        _catalog = catalog;
        _cancellationToken = cancellationToken;
        _snapshot = snapshot;
        _parameters = parameters;
        _estimateSinglePath = estimateSinglePath;
    }

    internal DocumentStatementPlan Plan(OqlExpression expression) => expression switch
    {
        OqlInsertExpression insert => PlanWrite(insert, insert.Collection, insert.Values, insert.Select, insert.KeyName, insert.ValueName),
        OqlUpsertExpression upsert => PlanWrite(upsert, upsert.Collection, upsert.Values, upsert.Select, upsert.KeyName, upsert.ValueName),
        OqlDeleteExpression delete => PlanDelete(delete),
        OqlUpdateExpression update => PlanUpdate(update),
        OqlSelectExpression select when DocumentSystemCollections.Find(select.Collection) is string name =>
            new DocumentSystemCollectionPlan(CreateLogicalPlan(select, _cancellationToken, _parameters), name),
        OqlSelectExpression select => Plan(select),
        OqlCreateCollectionExpression createCollection => PlanCreateCollection(createCollection),
        OqlDropCollectionExpression dropCollection => PlanDropCollection(dropCollection),
        OqlCreateIndexExpression createIndex => PlanCreateIndex(createIndex),
        OqlDropIndexExpression dropIndex => PlanDropIndex(dropIndex),
        _ => throw new DocumentPlanningException("CD05001", ErrorMessages.DocumentPlannerPlanFailure, expression.Location),
    };

    internal DocumentPlan Plan(OqlSelectExpression query)
    {
        var logical = CreateLogicalPlan(query, _cancellationToken, _parameters);
        var collection = ResolveCollection(query.Collection, query.Location);
        return new DocumentPlan(logical, collection, ChooseAccess(query.Predicate, query.Alias, _catalog.GetIndexes(collection.Id, _snapshot),
            logical.UnnestVariables));
    }

    private static DocumentCreateCollectionPlan PlanCreateCollection(OqlCreateCollectionExpression create)
    {
        ValidateCollectionName(create.Name, create);
        return new DocumentCreateCollectionPlan(create);
    }

    private static DocumentDropCollectionPlan PlanDropCollection(OqlDropCollectionExpression drop)
    {
        ValidateCollectionName(drop.Name, drop);
        return new DocumentDropCollectionPlan(drop);
    }

    private static void ValidateCollectionName(string name, OqlExpression expression)
    {
        // Match the session API's ArgumentException.ThrowIfNullOrWhiteSpace rule before either core runs.
        if (!string.IsNullOrWhiteSpace(name)) { return; }
        var location = expression.Location;
        if (expression.Text is { } text && location is not null)
        {
            // The last token is the name. Reuse the shared lexer only on this refusal path,
            // retaining the AST's statement span for other collection failures.
            foreach (var token in new TokenLexer(text, new TokenLexerOptions { Keywords = [] }))
            {
                int end = token.Position + token.Value.Length;
                if (end == location.End && token.Type == TokenType.QuotedIdentifier)
                {
                    location = Location.Create(1 + TokenLexer.CountLineBreaks(text.AsSpan(0, token.Position)),
                        location.EndLine, token.Position, end);
                    break;
                }
            }
        }
        throw new DocumentPlanningException("CD05001", ErrorMessages.CollectionNameMissing, location);
    }

    private DocumentCreateIndexPlan PlanCreateIndex(OqlCreateIndexExpression create)
    {
        DocumentSystemCollections.EnsureReadOnly(create.Collection, create.Location);
        if (string.IsNullOrWhiteSpace(create.IndexName))
        {
            throw new DocumentPlanningException("CD05001", ErrorMessages.DocumentPlannerPlanCreateIndexFailure, create.Location);
        }

        if (create.Paths.Count is < 1 or > 32)
        { throw new DocumentPlanningException("CD03022", ErrorMessages.IndexPathCount, create.Location); }
        var paths = create.Paths.Select(path => IndexPath(path, alias: null)
            ?? throw new DocumentPlanningException("CD05001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DocumentPlannerPlanCreateIndexFailure2, create.IndexName), path.Location)).ToArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
        { throw new DocumentPlanningException("CD05001", ErrorMessages.IndexPathsDuplicate, create.Location); }
        return new DocumentCreateIndexPlan(ResolveCollection(create.Collection, create.Location), create.IndexName, paths, create.IsUnique, create.IfNotExists);
    }

    private DocumentDropIndexPlan PlanDropIndex(OqlDropIndexExpression drop)
    {
        DocumentSystemCollections.EnsureReadOnly(drop.Collection, drop.Location);
        if (string.IsNullOrWhiteSpace(drop.IndexName))
        {
            throw new DocumentPlanningException("CD05001", ErrorMessages.DocumentPlannerPlanDropIndexFailure, drop.Location);
        }

        return new DocumentDropIndexPlan(ResolveCollection(drop.Collection, drop.Location), drop.IndexName, drop.IfExists);
    }

    private DocumentCollectionMetadata ResolveCollection(string name, Assimalign.Cohesion.Database.Language.Location? location)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DocumentPlanningException("CD05001", ErrorMessages.PlannerResolveCollectionFailure, location);
        }

        return _catalog.FindCollection(name, _snapshot)
            ?? throw new DocumentPlanningException("CD01002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CollectionDoesNotExist, name), location);
    }

    internal static DocumentLogicalPlan CreateLogicalPlan(OqlSelectExpression query, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters, cancellationToken);
        long? limit = evaluator.PagingCount(query.Limit, "LIMIT");
        long offset = evaluator.PagingCount(query.Offset, "OFFSET") ?? 0;
        var variables = query.Unnest.Count == 0 ? null : new HashSet<string>(StringComparer.Ordinal) { query.Alias ?? query.Collection };
        foreach (var binding in query.Unnest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(binding.Variable))
            { throw new DocumentPlanningException("CD01023", ErrorMessages.BindingNameMissing, binding.Location); }
            if (!variables!.Add(binding.Variable))
            { throw new DocumentPlanningException("CD01023", ErrorMessages.BindingNameDuplicate, binding.Location); }
            Validate(binding.Collection, allowAggregate: false);
            ValidateMetadata(binding.Collection, query.Alias, DocumentSystemCollections.Find(query.Collection) is null);
        }
        foreach (var expression in query.Projections.Select(p => p.Expression).Concat(query.GroupBy)
            .Concat(query.OrderBy.Select(o => o.Expression)).Concat(new[] { query.Predicate, query.Having }).OfType<OqlExpression>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMetadata(expression, query.Alias, DocumentSystemCollections.Find(query.Collection) is null);
        }
        if (query.Projections.Count == 0) { throw new DocumentPlanningException("CD05001", ErrorMessages.PlannerCreateLogicalPlanFailure, query.Location); }
        if (query.IsValue && (query.Projections.Count != 1 || query.Projections[0].Alias is not null ||
            query.Projections[0].Expression is OqlStarExpression))
        {
            throw new DocumentPlanningException("CD05001", ErrorMessages.ValueProjectionInvalid, query.Location);
        }
        // An explicit projection alias is visible to ORDER BY. Other paths
        // continue to address source documents, including unprojected fields.
        var ordering = query.OrderBy.Select(order => order.Expression is OqlPathExpression { Segments.Count: 1 } path &&
            query.Projections.FirstOrDefault(projection => projection.Alias is not null && projection.Alias == path.Segments[0].Name) is { } projection
                ? new OqlOrdering(projection.Expression, order.Descending) : order).ToArray();
        query = new OqlSelectExpression(query.Collection, query.Alias, query.Projections, query.Predicate,
            query.GroupBy, query.Having, ordering, query.IsDistinct, query.IsValue, query.Limit, query.Offset, query.Location, query.Unnest);
        bool grouped = query.GroupBy.Count > 0 || query.Projections.Any(projection => ContainsAggregate(projection.Expression)) ||
            (query.Having is not null && ContainsAggregate(query.Having)) || query.OrderBy.Any(order => ContainsAggregate(order.Expression));
        variables?.Remove(query.Alias ?? query.Collection);
        var unnestVariables = variables;
        var scopes = grouped || query.IsDistinct || query.IsValue && query.OrderBy.Count > 0 ? new DocumentBindingScopes() : null;
        if (query.Predicate is not null)
        {
            Validate(query.Predicate, allowAggregate: false);
        }
        foreach (var key in query.GroupBy) { Validate(key, allowAggregate: false); }
        if (query.Having is not null && !grouped) { throw new DocumentPlanningException("CD05001", ErrorMessages.PlannerCreateLogicalPlanFailure2, query.Having.Location); }
        var projections = new List<DocumentProjection>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projection in query.Projections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(projection.Expression, allowAggregate: true, allowStar: !grouped);
            if (grouped) { ValidateGrouped(projection.Expression, query, unnestVariables, scopes!); }
            string name = query.IsValue ? "value" : projection.Alias ?? projection.Expression switch
            {
                OqlStarExpression => "document",
                OqlPathExpression path => path.Segments.LastOrDefault().Name ?? $"column{projections.Count + 1}",
                OqlCallExpression call => call.Name.ToLowerInvariant(),
                OqlMetaExpression meta => meta.Member!,
                _ => $"column{projections.Count + 1}",
            };
            if (!names.Add(name))
            {
                throw new DocumentPlanningException("CD01003", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PlannerCreateLogicalPlanFailure3, name), projection.Expression.Location);
            }
            projections.Add(new DocumentProjection(name, projection.Expression, StaticType(projection.Expression)));
        }
        if (query.Having is not null)
        {
            Validate(query.Having, allowAggregate: true);
            ValidateGrouped(query.Having, query, unnestVariables, scopes!);
        }
        foreach (var order in query.OrderBy)
        {
            if (query.IsDistinct && !query.Projections.Any(projection =>
                EquivalentExpression(projection.Expression, order.Expression, query.Alias, unnestVariables, scopes!)))
            {
                throw new DocumentPlanningException("CD01014", ErrorMessages.DistinctOrderNotProjected, order.Expression.Location);
            }
            Validate(order.Expression, allowAggregate: true);
            if (grouped) { ValidateGrouped(order.Expression, query, unnestVariables, scopes!); }
        }
        bool canStopEarly = !grouped && !query.IsDistinct && query.OrderBy.Count == 0 &&
            projections.All(projection => (projection.Expression is not OqlStarExpression || query.Unnest.Count > 0) &&
                (projection.Type != DatabaseType.Null || projection.Expression is
                    OqlLiteralExpression { Value: null or JsonElement { ValueKind: JsonValueKind.Null } } or OqlMissingExpression));
        var projectedOrdering = query.IsValue ? query.OrderBy.Select(order =>
            EquivalentExpression(query.Projections[0].Expression, order.Expression, query.Alias, unnestVariables, scopes!)).ToArray() : [];
        return new DocumentLogicalPlan(query, projections, grouped, limit, offset, unnestVariables, canStopEarly, projectedOrdering);
    }

    private DocumentAccessPath ChooseAccess(OqlExpression? predicate, string? alias, IReadOnlyList<DocumentIndexMetadata> indexes,
        IReadOnlySet<string>? variables = null)
    {
        var predicates = new List<(string Path, string? LegacyPath, string Operator, object Value)>();
        if (predicate is not null) { Collect(predicate); }
        DocumentIndexPath? best = null;
        int bestPrefix = -1;
        bool bestRange = false;
        long bestCost = long.MaxValue;
        var candidates = new List<(DocumentIndexPath Path, int Prefix, bool Range)>();
        foreach (var index in indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var prefix = new List<object>();
            DocumentSeekBound? lower = null;
            DocumentSeekBound? upper = null;
            foreach (string path in index.Paths)
            {
                var usable = predicates.Where(term => term.Path == path || term.LegacyPath == path).ToArray();
                var equality = usable.FirstOrDefault(term => term.Operator == "=");
                if (equality.Value is not null) { prefix.Add(equality.Value); continue; }
                foreach (var term in usable)
                {
                    if (term.Operator is ">" or ">=")
                    { lower = Tighter(lower, new DocumentSeekBound(term.Value, term.Operator == ">="), isLower: true); }
                    if (term.Operator is "<" or "<=")
                    { upper = Tighter(upper, new DocumentSeekBound(term.Value, term.Operator == "<="), isLower: false); }
                }
                break;
            }
            bool range = lower is not null || upper is not null;
            if (prefix.Count == 0 && !range) { continue; }
            // Keep the single-path plan's scalar bound shape for callers inspecting it.
            DocumentIndexPath candidate;
            if (index.Paths.Count == 1 && prefix.Count == 1)
            { candidate = new DocumentIndexPath(index, new DocumentSeekBound(prefix[0], true), new DocumentSeekBound(prefix[0], true)); }
            else { candidate = new DocumentIndexPath(index, lower, upper, prefix.AsReadOnly()); }
            candidates.Add((candidate, prefix.Count, range));
        }
        if (candidates.Count == 0) { return new DocumentScanPath(); }
        if (candidates.Count == 1 && !_estimateSinglePath) { return candidates[0].Path; }
        var probed = new List<DocumentIndexPath>(candidates.Count);
        foreach (var (path, prefix, range) in candidates)
        {
            var index = path.Index;
            var estimate = _catalog.CountIndexRange(index, path.EqualityPrefix ?? [], path.Lower?.Value,
                path.Lower?.Inclusive ?? false, path.Upper?.Value, path.Upper?.Inclusive ?? false,
                _snapshot, _cancellationToken);
            var candidate = path with { Estimate = estimate };
            probed.Add(candidate);
            long cost = estimate.IsComplete ? estimate.Count : Math.Max(1024, estimate.Count * 2);
            bool better = best is null || cost < bestCost || cost == bestCost &&
                (prefix > bestPrefix || prefix == bestPrefix &&
                    (range && !bestRange || range == bestRange && index.IsUnique && !best.Index.IsUnique));
            if (!better) { continue; }
            best = candidate;
            bestCost = cost;
            bestPrefix = prefix;
            bestRange = range;
        }
        // Exact, comparable inputs bound the extra index work. Tiny selective inputs
        // are cheaper to filter directly; incomplete probes retain a single driver.
        var selected = best!;
        if (selected.Estimate is not { IsComplete: true, Count: >= 16 }) { return selected; }
        var inputs = new List<DocumentIndexPath> { selected };
        var covered = new HashSet<string>(ConstrainedPaths(selected), StringComparer.Ordinal);
        foreach (var candidate in probed.OrderBy(path => path.Estimate!.Value.Count)
            .ThenBy(path => path.Index.Name, StringComparer.Ordinal))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(candidate, selected) || candidate.Estimate is not { IsComplete: true } count ||
                count.Count > bestCost * 2) { continue; }
            var paths = ConstrainedPaths(candidate).ToArray();
            if (paths.All(covered.Contains)) { continue; }
            inputs.Add(candidate);
            covered.UnionWith(paths);
            if (inputs.Count == 4) { break; }
        }
        return inputs.Count == 1 ? selected : new DocumentIntersectionPath(inputs.AsReadOnly());

        void Collect(OqlExpression expression)
        {
            if (expression is OqlBetweenExpression { Negated: false, Operand: OqlPathExpression rangePath } between &&
                IsConstant(between.Lower) && IsConstant(between.Upper))
            {
                if (IsVariablePath(rangePath, variables)) { return; }
                var rangeEvaluator = new DocumentExpressionEvaluator(alias, _parameters, _cancellationToken);
                var lower = rangeEvaluator.Evaluate(between.Lower, default);
                var upper = rangeEvaluator.Evaluate(between.Upper, default);
                string? name = IndexPath(rangePath, alias);
                if (name is not null && lower is bool or decimal or string && upper is bool or decimal or string)
                {
                    var legacy = LegacyIndexPath(rangePath, alias);
                    predicates.Add((name, legacy, ">=", lower));
                    predicates.Add((name, legacy, "<=", upper));
                }
                return;
            }
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
            if (path is null || IsVariablePath(path, variables) || !IsConstant(constant)) { return; }
            var evaluator = new DocumentExpressionEvaluator(alias, _parameters, _cancellationToken);
            var value = evaluator.Evaluate(constant, default);
            string? pathName = IndexPath(path, alias);
            if (pathName is not null && value is bool or decimal or string)
            {
                predicates.Add((pathName, LegacyIndexPath(path, alias), operation, value));
            }
        }
    }

    private static IEnumerable<string> ConstrainedPaths(DocumentIndexPath path)
    {
        int count = path.EqualityPrefix?.Count ?? 0;
        if (path.Lower is not null || path.Upper is not null) { count++; }
        for (int i = 0; i < count; i++) { yield return path.Index.Paths[i]; }
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

    internal static bool IsConstant(OqlExpression expression) => expression switch
    {
        OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression => PredicateChildren(expression).All(IsConstant),
        OqlLiteralExpression or OqlMissingExpression or OqlParameterExpression => true,
        OqlArrayExpression array => array.Elements.All(IsConstant),
        OqlObjectExpression obj => obj.Members.All(member => IsConstant(member.Value)),
        OqlUnaryExpression unary => IsConstant(unary.Operand),
        OqlBinaryExpression binary => IsConstant(binary.Left) && IsConstant(binary.Right),
        _ => false,
    };

    internal static bool ContainsAggregate(OqlExpression expression) => expression switch
    {
        OqlQuantifiedExpression or OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression => PredicateChildren(expression).Any(ContainsAggregate),
        OqlArrayExpression array => array.Elements.Any(ContainsAggregate),
        OqlObjectExpression obj => obj.Members.Any(member => ContainsAggregate(member.Value)),
        OqlCallExpression call => OqlFunctions.Find(call.Name)?.IsAggregate == true || call.Arguments.Any(ContainsAggregate),
        OqlBinaryExpression binary => ContainsAggregate(binary.Left) || ContainsAggregate(binary.Right),
        OqlUnaryExpression unary => ContainsAggregate(unary.Operand),
        _ => false,
    };

    private static void Validate(OqlExpression expression, bool allowAggregate, bool allowStar = false)
    {
        switch (expression)
        {
            case OqlQuantifiedExpression quantified:
                Validate(quantified.Collection, allowAggregate: false);
                Validate(quantified.Predicate, allowAggregate: false);
                return;
            case OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression:
                foreach (var child in PredicateChildren(expression)) { Validate(child, allowAggregate); }
                return;
            case OqlLiteralExpression or OqlMissingExpression or OqlParameterExpression or OqlPathExpression or OqlMetaExpression:
                return;
            case OqlArrayExpression array:
                foreach (var element in array.Elements) { Validate(element, allowAggregate); }
                return;
            case OqlObjectExpression obj:
                foreach (var member in obj.Members) { Validate(member.Value, allowAggregate); }
                return;
            case OqlStarExpression when allowStar:
                return;
            case OqlBinaryExpression binary when binary.Operator is "AND" or "OR" or "=" or "!=" or "<>" or "<" or "<=" or ">" or ">=" or "+" or "-" or "*" or "/" or "%":
                Validate(binary.Left, allowAggregate);
                Validate(binary.Right, allowAggregate);
                return;
            case OqlUnaryExpression unary when unary.Operator is "+" or "-" or "NOT" or "IS NULL" or "IS NOT NULL" or
                "IS MISSING" or "IS NOT MISSING" or "IS VALUED" or "IS NOT VALUED":
                Validate(unary.Operand, allowAggregate);
                return;
            case OqlCallExpression call when call.Name != "META" && OqlFunctions.Find(call.Name) is { } function &&
                (!function.IsAggregate || allowAggregate):
                if (!function.AcceptsArity(call.Arguments.Count))
                {
                    throw new DocumentPlanningException("CD05001", ErrorMessages.FunctionArgumentsInvalid, call.Location);
                }
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    Validate(call.Arguments[i], allowAggregate: !function.IsAggregate && allowAggregate, allowStar: call.Name == "COUNT");
                    ValidateFunctionKind(function, i, call.Arguments[i]);
                }
                return;
            default:
                throw new DocumentPlanningException("CD05001", ErrorMessages.DocumentPlannerValidateFailure, expression.Location);
        }
    }

    private static void ValidateGrouped(OqlExpression expression, OqlSelectExpression query, IReadOnlySet<string>? variables,
        DocumentBindingScopes scopes, DocumentBindingScope? bound = null)
    {
        if (expression is OqlPathExpression { Segments.Count: > 0 } path &&
            DocumentBindingScope.Resolve(bound, path.Segments[0].Name, out _) is not null) { return; }
        // PostgreSQL parse_agg.c:1439-1452 adjusts grouping expressions to the current
        // scope before matching. An inner t must never stand in for an outer field t.
        if (query.GroupBy.Any(key => EquivalentExpression(key, expression, query.Alias, variables, scopes, rightScope: bound))) { return; }
        if (expression is OqlQuantifiedExpression quantified)
        {
            ValidateGrouped(quantified.Collection, query, variables, scopes, bound);
            ValidateGrouped(quantified.Predicate, query, variables, scopes, scopes.Enter(quantified, bound));
            return;
        }
        if (expression is OqlInExpression or OqlBetweenExpression or OqlLikeExpression or OqlCaseExpression)
        {
            foreach (var child in PredicateChildren(expression)) { ValidateGrouped(child, query, variables, scopes, bound); }
            return;
        }
        if (expression is OqlArrayExpression array)
        {
            foreach (var element in array.Elements) { ValidateGrouped(element, query, variables, scopes, bound); }
            return;
        }
        if (expression is OqlObjectExpression obj)
        {
            foreach (var member in obj.Members) { ValidateGrouped(member.Value, query, variables, scopes, bound); }
            return;
        }
        if (expression is OqlCallExpression call)
        {
            if (OqlFunctions.Find(call.Name)?.IsAggregate == true) { return; }
            foreach (var argument in call.Arguments) { ValidateGrouped(argument, query, variables, scopes, bound); }
            return;
        }
        if (expression is OqlLiteralExpression or OqlMissingExpression or OqlParameterExpression) { return; }
        if (expression is OqlBinaryExpression binary)
        {
            ValidateGrouped(binary.Left, query, variables, scopes, bound);
            ValidateGrouped(binary.Right, query, variables, scopes, bound);
            return;
        }
        if (expression is OqlUnaryExpression unary) { ValidateGrouped(unary.Operand, query, variables, scopes, bound); return; }
        throw new DocumentPlanningException("CD05001", ErrorMessages.DocumentPlannerValidateGroupedFailure, expression.Location);
    }

    private static bool IsVariablePath(OqlPathExpression path, IReadOnlySet<string>? variables)
        => path.Segments.Count > 0 && path.Segments[0].Name is string name && variables?.Contains(name) == true;

    private static IEnumerable<OqlPathSegment> PathSegments(OqlPathExpression path, string? alias)
        => path.Segments.Skip(alias is not null && path.Segments.Count > 0 && path.Segments[0].Name == alias ? 1 : 0);

    private static bool EquivalentExpression(OqlExpression left, OqlExpression right, string? alias, IReadOnlySet<string>? variables,
        DocumentBindingScopes scopes, DocumentBindingScope? leftScope = null, DocumentBindingScope? rightScope = null, int localDepth = 0) => (left, right) switch
    {
        (OqlQuantifiedExpression a, OqlQuantifiedExpression b) => a.IsEvery == b.IsEvery && a.Variable == b.Variable &&
            EquivalentExpression(a.Collection, b.Collection, alias, variables, scopes, leftScope, rightScope, localDepth) &&
            EquivalentExpression(a.Predicate, b.Predicate, alias, variables, scopes,
                scopes.Enter(a, leftScope), scopes.Enter(b, rightScope), localDepth + 1),
        (OqlInExpression a, OqlInExpression b) => (a.Array is null) == (b.Array is null) && a.Negated == b.Negated && EquivalentChildren(a, b, alias, variables, scopes, leftScope, rightScope, localDepth),
        (OqlBetweenExpression a, OqlBetweenExpression b) => a.Negated == b.Negated && EquivalentChildren(a, b, alias, variables, scopes, leftScope, rightScope, localDepth),
        (OqlLikeExpression a, OqlLikeExpression b) => a.Negated == b.Negated && EquivalentChildren(a, b, alias, variables, scopes, leftScope, rightScope, localDepth),
        (OqlCaseExpression a, OqlCaseExpression b) => (a.Operand is null) == (b.Operand is null) &&
            (a.Otherwise is null) == (b.Otherwise is null) && a.Branches.Count == b.Branches.Count && EquivalentChildren(a, b, alias, variables, scopes, leftScope, rightScope, localDepth),
        (OqlPathExpression a, OqlPathExpression b) => EquivalentPath(a, b, alias, variables, leftScope, rightScope, localDepth),
        (OqlLiteralExpression a, OqlLiteralExpression b) => DocumentExpressionEvaluator.Compare(a.Value, b.Value) == 0,
        (OqlCallExpression a, OqlCallExpression b) => a.Name == b.Name && a.Arguments.Count == b.Arguments.Count &&
            a.Arguments.Zip(b.Arguments).All(pair => EquivalentExpression(pair.First, pair.Second, alias, variables, scopes, leftScope, rightScope, localDepth)),
        (OqlStarExpression, OqlStarExpression) => true,
        (OqlArrayExpression a, OqlArrayExpression b) => a.Elements.Count == b.Elements.Count &&
            a.Elements.Zip(b.Elements).All(pair => EquivalentExpression(pair.First, pair.Second, alias, variables, scopes, leftScope, rightScope, localDepth)),
        (OqlMetaExpression a, OqlMetaExpression b) => a.Member == b.Member,
        (OqlObjectExpression a, OqlObjectExpression b) => a.Members.Count == b.Members.Count &&
            a.Members.Zip(b.Members).All(pair => pair.First.Key == pair.Second.Key &&
                EquivalentExpression(pair.First.Value, pair.Second.Value, alias, variables, scopes, leftScope, rightScope, localDepth)),
        (OqlMissingExpression, OqlMissingExpression) => true,
        (OqlParameterExpression a, OqlParameterExpression b) => a.Name == b.Name,
        (OqlBinaryExpression a, OqlBinaryExpression b) => a.Operator == b.Operator &&
            EquivalentExpression(a.Left, b.Left, alias, variables, scopes, leftScope, rightScope, localDepth) &&
            EquivalentExpression(a.Right, b.Right, alias, variables, scopes, leftScope, rightScope, localDepth),
        (OqlUnaryExpression a, OqlUnaryExpression b) => a.Operator == b.Operator && EquivalentExpression(a.Operand, b.Operand, alias, variables, scopes, leftScope, rightScope, localDepth),
        _ => false,
    };

    private static bool EquivalentPath(OqlPathExpression left, OqlPathExpression right, string? alias, IReadOnlySet<string>? variables,
        DocumentBindingScope? leftScope, DocumentBindingScope? rightScope, int localDepth)
    {
        string? leftName = left.Segments.Count > 0 ? left.Segments[0].Name : null;
        string? rightName = right.Segments.Count > 0 ? right.Segments[0].Name : null;
        var leftBinding = DocumentBindingScope.Resolve(leftScope, leftName, out int leftDistance);
        var rightBinding = DocumentBindingScope.Resolve(rightScope, rightName, out int rightDistance);
        if (leftBinding is not null || rightBinding is not null)
        {
            // Corresponding quantifiers inside the expressions introduce equivalent local
            // bindings. Bindings enclosing only one expression retain their own identity.
            return leftBinding is not null && rightBinding is not null &&
                (ReferenceEquals(leftBinding, rightBinding) || leftDistance < localDepth && leftDistance == rightDistance) &&
                left.Segments.SequenceEqual(right.Segments);
        }
        if (IsVariablePath(left, variables) != IsVariablePath(right, variables)) { return false; }
        int leftStart = !IsVariablePath(left, variables) && alias is not null && leftName == alias ? 1 : 0;
        int rightStart = !IsVariablePath(right, variables) && alias is not null && rightName == alias ? 1 : 0;
        return left.Segments.Skip(leftStart).SequenceEqual(right.Segments.Skip(rightStart));
    }

    private static bool EquivalentChildren(OqlExpression left, OqlExpression right, string? alias, IReadOnlySet<string>? variables,
        DocumentBindingScopes scopes, DocumentBindingScope? leftScope, DocumentBindingScope? rightScope, int localDepth)
    {
        var a = PredicateChildren(left).ToArray();
        var b = PredicateChildren(right).ToArray();
        return a.Length == b.Length && a.Zip(b).All(pair => EquivalentExpression(pair.First, pair.Second, alias, variables, scopes, leftScope, rightScope, localDepth));
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanner value)
        {
            _ = value;
        }
    }
}
