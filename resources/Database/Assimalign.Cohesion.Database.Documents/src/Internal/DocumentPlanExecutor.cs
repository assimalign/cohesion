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

internal static partial class DocumentPlanExecutor
{
    internal static async ValueTask<DatabaseResult> ExecuteAsync(DocumentDatabase database, DocumentOperation operation,
        OqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        operation.EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null) { throw new DatabaseParseException($"OQL parse error {error.Code}: {error.Message}", statement.Diagnostics); }
        var plan = new DocumentPlanner(database.Catalog, operation.Context.Snapshot, parameters, cancellationToken).Plan(statement.OqlExpression);
        return plan switch
        {
            DocumentDeletePlan delete => await ExecuteDeleteAsync(database, operation, delete, parameters, cancellationToken).ConfigureAwait(false),
            DocumentUpdatePlan update => await ExecuteUpdateAsync(database, operation, update, parameters, cancellationToken).ConfigureAwait(false),
            DocumentInsertPlan insert => await ExecuteWriteAsync(database, operation, insert.Collection, insert.Statement, insert.Statement.Values, null, true, parameters, cancellationToken).ConfigureAwait(false),
            DocumentInsertSelectPlan insert => await ExecuteWriteAsync(database, operation, insert.Collection, insert.Statement, insert.Statement.Values, (insert.Source, insert.KeyOrdinal, insert.ValueOrdinal), true, parameters, cancellationToken).ConfigureAwait(false),
            DocumentUpsertPlan upsert => await ExecuteWriteAsync(database, operation, upsert.Collection, upsert.Statement,
                upsert.Statement.Values, upsert.Source is { } source ? (source, upsert.KeyOrdinal, upsert.ValueOrdinal) : null,
                false, parameters, cancellationToken).ConfigureAwait(false),
            DocumentSystemCollectionPlan system => ExecuteSystemCollection(database, operation, system, parameters, cancellationToken),
            DocumentPlan select => await ExecuteSelectAsync(database, operation, select, parameters, cancellationToken).ConfigureAwait(false),
            DocumentCreateCollectionPlan createCollection => await ExecuteCreateCollectionAsync(database, operation, createCollection, cancellationToken).ConfigureAwait(false),
            DocumentDropCollectionPlan dropCollection => await ExecuteDropCollectionAsync(database, operation, dropCollection, cancellationToken).ConfigureAwait(false),
            DocumentCreateIndexPlan createIndex => await ExecuteCreateIndexAsync(database, operation, createIndex, cancellationToken).ConfigureAwait(false),
            DocumentDropIndexPlan dropIndex => await ExecuteDropIndexAsync(database, operation, dropIndex, cancellationToken).ConfigureAwait(false),
            _ => throw new DocumentPlanningException("COHDOC001", "The document plan is not executable.", statement.OqlExpression.Location),
        };
    }

    private static async ValueTask<DatabaseResult> ExecuteSelectAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, bool forInsert = false)
    {
        var query = plan.Logical.Query;
        var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters, cancellationToken, forInsert);
        var candidates = await ReadCandidatesAsync(database, operation, plan.Collection, plan.Access, cancellationToken).ConfigureAwait(false);

        var matches = new List<DocumentEvaluationContext>();
        // Index and scan order are deliberately erased. Identity order is the
        // baseline for row output and the tie break of every subsequent sort.
        foreach (var entry in candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            var document = database.ReadDocument(entry);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            var context = new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version,
                query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression ? document.Content : default);
            if (evaluator.Matches(query.Predicate, context)) { matches.Add(context with { Content = json.RootElement.Clone() }); }
        }

        return ExecuteRows(plan.Logical, matches, evaluator, cancellationToken);
    }

    private static ValueTask<IReadOnlyList<DocumentCatalogEntry>> ReadCandidatesAsync(DocumentDatabase database,
        DocumentOperation operation, DocumentCollectionMetadata collection, DocumentAccessPath access, CancellationToken cancellationToken)
        => access is DocumentIndexPath seek
            ? database.Catalog.SearchIndexAsync(collection.Id, seek.Index.Name,
                seek.Lower?.Value, seek.Lower?.Inclusive ?? false, seek.Upper?.Value, seek.Upper?.Inclusive ?? false,
                operation.Context.Snapshot, cancellationToken)
            : ValueTask.FromResult(database.Catalog.GetDocuments(collection.Id, null, operation.Context.Snapshot));

    private static DatabaseResult ExecuteSystemCollection(DocumentDatabase database, DocumentOperation operation,
        DocumentSystemCollectionPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, bool forInsert = false)
    {
        var evaluator = new DocumentExpressionEvaluator(plan.Logical.Query.Alias, parameters, cancellationToken, forInsert);
        var matches = new List<DocumentEvaluationContext>();
        foreach (var document in DocumentSystemCollections.Enumerate(database, operation.Context.Snapshot, plan.Name, cancellationToken))
        {
            operation.EnsureActive();
            if (evaluator.Matches(plan.Logical.Query.Predicate, new DocumentEvaluationContext(document))) { matches.Add(new DocumentEvaluationContext(document)); }
        }
        return ExecuteRows(plan.Logical, matches, evaluator, cancellationToken);
    }

    private static DatabaseResult ExecuteRows(DocumentLogicalPlan logical, List<DocumentEvaluationContext> matches,
        DocumentExpressionEvaluator evaluator, CancellationToken cancellationToken)
    {
        var query = logical.Query;
        var output = new List<EvaluatedRow>();
        if (logical.IsGrouped)
        {
            var groups = new SortedDictionary<object?[], List<DocumentEvaluationContext>>(TupleComparer.Instance);
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
        if (query.IsDistinct)
        {
            var seen = new SortedSet<object?[]>(TupleComparer.Instance);
            var distinct = new List<EvaluatedRow>();
            foreach (var row in output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(row.Values)) { distinct.Add(row); }
            }
            output = distinct;
        }
        if (query.OrderBy.Count > 0)
        {
            foreach (var row in output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                row.OrderKeys = query.OrderBy.Select(order => query.IsValue &&
                    DocumentPlanner.EquivalentExpression(query.Projections[0].Expression, order.Expression, query.Alias)
                        ? row.Values[0] : evaluator.Evaluate(order.Expression, row.Document, row.Group)).ToArray();
            }
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
        var columns = new DatabaseColumn[logical.Projections.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            var types = output.Select(row => GetType(row.Values[i])).Where(type => type != DatabaseType.Null).Distinct().ToArray();
            columns[i] = new DatabaseColumn { Name = logical.Projections[i].Name, Ordinal = i, Type = types.Length == 1 ? types[0] : DatabaseType.Null };
        }
        bool wholeDocument = query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression;
        return new DocumentQueryResult(columns, output.Select(row => row.Values).ToList(),
            query.IsValue || wholeDocument, wholeDocument ? output.Select(row => row.Document.RawContent).ToArray() : null);

        void Project(DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
        {
            var values = logical.Projections.Select(projection => evaluator.EvaluateProjection(projection.Expression, document, group, query.IsValue)).ToArray();
            // Normalize the JSON root before DISTINCT, projected ordering and counting.
            if (query.IsValue && values[0] is DocumentMissing) { values[0] = null; }
            output.Add(new EvaluatedRow(values, document, group, output.Count));
        }
    }

    private static async ValueTask<DatabaseResult> ExecuteCreateCollectionAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentCreateCollectionPlan plan, CancellationToken cancellationToken)
    {
        var create = plan.Statement;
        await database.CreateCollectionCoreAsync(create.Name, create.IfNotExists, operation, cancellationToken, create.Location).ConfigureAwait(false);
        return DocumentCommandResult.Success;
    }

    private static async ValueTask<DatabaseResult> ExecuteDropCollectionAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentDropCollectionPlan plan, CancellationToken cancellationToken)
    {
        var drop = plan.Statement;
        await database.DropCollectionCoreAsync(drop.Name, drop.IfExists, operation, cancellationToken, drop.Location).ConfigureAwait(false);
        return DocumentCommandResult.Success;
    }

    private static async ValueTask<DatabaseResult> ExecuteCreateIndexAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentCreateIndexPlan plan, CancellationToken cancellationToken)
    {
        await PrepareIndexChangeAsync(database, operation, plan.Collection, "CREATE INDEX", cancellationToken).ConfigureAwait(false);
        await database.Catalog.CreateIndexAsync(plan.Collection.Id, plan.IndexName, plan.Path,
            operation.Context, cancellationToken).ConfigureAwait(false);
        return DocumentCommandResult.Success;
    }

    private static async ValueTask<DatabaseResult> ExecuteDropIndexAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentDropIndexPlan plan, CancellationToken cancellationToken)
    {
        await PrepareIndexChangeAsync(database, operation, plan.Collection, "DROP INDEX", cancellationToken).ConfigureAwait(false);
        await database.Catalog.DeleteIndexAsync(plan.Collection.Id, plan.IndexName,
            operation.Context, cancellationToken).ConfigureAwait(false);
        return DocumentCommandResult.Success;
    }

    private static async ValueTask PrepareIndexChangeAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentCollectionMetadata collection, string operationName, CancellationToken cancellationToken)
    {
        await database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
        if (collection != database.Catalog.FindCollection(collection.Name, database.LatestSnapshot(operation.Context)))
        {
            DocumentDatabase.ThrowConflict();
        }
        if (collection.Owner == DatabaseObjectOwner.Schema)
        {
            throw new DatabaseObjectLockedException(collection.Name, collection.OwningSchema!, operationName);
        }
        if (!database.Catalog.GetDocuments(collection.Id, null, operation.Context.Snapshot)
                .SequenceEqual(database.Catalog.GetDocuments(collection.Id, null, database.LatestSnapshot(operation.Context))) ||
            !database.Catalog.GetIndexes(collection.Id, operation.Context.Snapshot)
                .SequenceEqual(database.Catalog.GetIndexes(collection.Id, database.LatestSnapshot(operation.Context))))
        {
            DocumentDatabase.ThrowConflict();
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

    private sealed record EvaluatedRow(object?[] Values, DocumentEvaluationContext Document, IReadOnlyList<DocumentEvaluationContext>? Group, int Ordinal)
    {
        internal object?[] OrderKeys { get; set; } = [];
    }

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
