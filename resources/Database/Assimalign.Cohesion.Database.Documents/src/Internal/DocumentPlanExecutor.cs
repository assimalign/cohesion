using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentPlanExecutor
{
    internal static async ValueTask<QueryResult> ExecuteAsync(DocumentDatabaseInstance database, DocumentOperation operation,
        OqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        operation.EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null) { throw new DatabaseParseException($"OQL parse error {error.Code}: {error.Message}"); }
        var plan = new DocumentPlanner(database.Catalog, operation.Context.Snapshot, parameters).Plan(statement.OqlExpression);
        var query = plan.Logical.Query;
        var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters);
        IReadOnlyList<DocumentCatalogEntry> candidates = plan.Access is DocumentIndexPath seek
            ? await database.Catalog.SearchIndexAsync(plan.Collection.Id, seek.Index.Name,
                seek.Lower?.Value, seek.Lower?.Inclusive ?? false, seek.Upper?.Value, seek.Upper?.Inclusive ?? false,
                operation.Context.Snapshot, cancellationToken).ConfigureAwait(false)
            : database.Catalog.GetDocuments(plan.Collection.Id, null, operation.Context.Snapshot);

        var matches = new List<JsonElement>();
        // Index and scan order are deliberately erased. Identity order is the
        // baseline for row output and the tie break of every subsequent sort.
        foreach (var entry in candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            var document = database.ReadDocument(entry);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            if (evaluator.Matches(query.Predicate, json.RootElement)) { matches.Add(json.RootElement.Clone()); }
        }

        var output = new List<EvaluatedRow>();
        if (plan.Logical.IsGrouped)
        {
            var groups = new SortedDictionary<object?[], List<JsonElement>>(TupleComparer.Instance);
            if (query.GroupBy.Count == 0) { groups.Add([], matches); }
            else
            {
                foreach (var document in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = query.GroupBy.Select(expression => evaluator.Evaluate(expression, document)).ToArray();
                    if (!groups.TryGetValue(key, out var group)) { groups.Add(key, group = []); }
                    group.Add(document);
                }
            }
            foreach (var group in groups.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = group.Count > 0 ? group[0] : default;
                if (evaluator.Matches(query.Having, document, group)) { Project(document, group); }
            }
        }
        else
        {
            foreach (var document in matches) { cancellationToken.ThrowIfCancellationRequested(); Project(document, null); }
        }
        if (query.OrderBy.Count > 0)
        {
            output.Sort((left, right) =>
            {
                for (int i = 0; i < query.OrderBy.Count; i++)
                {
                    int comparison = DocumentExpressionEvaluator.Compare(left.OrderKeys[i], right.OrderKeys[i]);
                    if (comparison != 0) { return query.OrderBy[i].Descending ? -comparison : comparison; }
                }
                return left.Ordinal.CompareTo(right.Ordinal);
            });
        }
        var columns = new QueryColumn[plan.Logical.Projections.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            var types = output.Select(row => GetType(row.Values[i])).Where(type => type != DatabaseType.Null).Distinct().ToArray();
            columns[i] = new QueryColumn { Name = plan.Logical.Projections[i].Name, Ordinal = i, Type = types.Length == 1 ? types[0] : DatabaseType.Null };
        }
        return new DocumentQueryResult(columns, output.Select(row => row.Values).ToList());

        void Project(JsonElement document, IReadOnlyList<JsonElement>? group)
        {
            var values = plan.Logical.Projections.Select(projection => evaluator.Evaluate(projection.Expression, document, group)).ToArray();
            var orderKeys = query.OrderBy.Select(order => evaluator.Evaluate(order.Expression, document, group)).ToArray();
            output.Add(new EvaluatedRow(values, orderKeys, output.Count));
        }
    }

    private static DatabaseType GetType(object? value) => value switch
    {
        bool => DatabaseType.Boolean,
        decimal => DatabaseType.Decimal,
        string => DatabaseType.String,
        JsonElement => DatabaseType.Json,
        _ => DatabaseType.Null,
    };

    private sealed record EvaluatedRow(object?[] Values, object?[] OrderKeys, int Ordinal);

    private sealed class TupleComparer : IComparer<object?[]>
    {
        internal static TupleComparer Instance { get; } = new();
        public int Compare(object?[]? left, object?[]? right)
        {
            if (left is null) { return right is null ? 0 : -1; }
            if (right is null) { return 1; }
            for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                int comparison = DocumentExpressionEvaluator.Compare(left[i], right[i]);
                if (comparison != 0) { return comparison; }
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
