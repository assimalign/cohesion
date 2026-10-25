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

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    internal static async ValueTask<DatabaseResult> ExecuteAsync(DocumentDatabase database, DocumentOperation operation,
        OqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        operation.EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null) { throw new DatabaseParseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryRequestFromOqlDatabaseParse, error.Code, error.Message), statement.Diagnostics); }
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
            _ => throw new DocumentPlanningException("CD05001", ErrorMessages.PlanExecutorExecuteAsyncFailure, statement.OqlExpression.Location),
        };
    }

    private static async ValueTask<DatabaseResult> ExecuteSelectAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, string? writeVerb = null)
    {
        var query = plan.Logical.Query;
        var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters, cancellationToken, writeVerb, operation.TransactionStart,
            query.Unnest.Count > 0 ? query.Alias ?? query.Collection : null);
        var candidates = await ReadCandidatesAsync(database, operation, plan.Collection, plan.Access, cancellationToken).ConfigureAwait(false);

        var matches = new List<DocumentEvaluationContext>();
        // Index and scan order are deliberately erased. Identity order is the
        // baseline for row output and the tie break of every subsequent sort.
        foreach (var entry in candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            if (PageComplete(plan.Logical, matches.Count)) { break; }
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            operation.Session.LastQueryDocumentsRead++;
            var document = database.ReadDocument(entry);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            var context = new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version,
                query.Unnest.Count == 0 && query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression ? document.Content : default);
            if (query.Unnest.Count == 0)
            {
                if (evaluator.Matches(query.Predicate, context)) { matches.Add(context with { Content = json.RootElement.Clone() }); }
            }
            else
            {
                CollectMatches(plan.Logical, context with { Content = json.RootElement.Clone() }, matches, evaluator, operation, cancellationToken);
            }
        }

        return ExecuteRows(plan.Logical, matches, evaluator, cancellationToken);
    }

    private static ValueTask<IReadOnlyList<DocumentCatalogEntry>> ReadCandidatesAsync(DocumentDatabase database,
        DocumentOperation operation, DocumentCollectionMetadata collection, DocumentAccessPath access, CancellationToken cancellationToken)
        => access switch
        {
            DocumentIndexPath seek => database.Catalog.SearchIndexAsync(collection.Id, seek.Index.Name, seek.EqualityPrefix ?? [],
                seek.Lower?.Value, seek.Lower?.Inclusive ?? false, seek.Upper?.Value, seek.Upper?.Inclusive ?? false,
                operation.Context.Snapshot, cancellationToken),
            DocumentIntersectionPath intersection => ReadIntersectionAsync(database, operation, collection, intersection, cancellationToken),
            _ => ValueTask.FromResult(database.Catalog.GetDocuments(collection.Id, null, operation.Context.Snapshot)),
        };

    private static async ValueTask<IReadOnlyList<DocumentCatalogEntry>> ReadIntersectionAsync(DocumentDatabase database,
        DocumentOperation operation, DocumentCollectionMetadata collection, DocumentIntersectionPath intersection,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentCatalogEntry>? matches = null;
        foreach (var input in intersection.Inputs)
        {
            operation.EnsureActive();
            var candidates = await ReadCandidatesAsync(database, operation, collection, input, cancellationToken).ConfigureAwait(false);
            if (matches is null) { matches = candidates; continue; }
            var merged = new List<DocumentCatalogEntry>(Math.Min(matches.Count, candidates.Count));
            int left = 0, right = 0;
            while (left < matches.Count && right < candidates.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation.EnsureActive();
                int comparison = string.CompareOrdinal(matches[left].Id, candidates[right].Id);
                if (comparison == 0) { merged.Add(matches[left++]); right++; }
                else if (comparison < 0) { left++; }
                else { right++; }
            }
            matches = merged;
            if (matches.Count == 0) { break; }
        }
        return matches ?? Array.Empty<DocumentCatalogEntry>();
    }

    private static DatabaseResult ExecuteSystemCollection(DocumentDatabase database, DocumentOperation operation,
        DocumentSystemCollectionPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, string? writeVerb = null)
    {
        var query = plan.Logical.Query;
        var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters, cancellationToken, writeVerb, operation.TransactionStart,
            query.Unnest.Count > 0 ? query.Alias ?? query.Collection : null);
        var matches = new List<DocumentEvaluationContext>();
        foreach (var document in DocumentSystemCollections.Enumerate(database, operation.Context.Snapshot, plan.Name, cancellationToken))
        {
            if (PageComplete(plan.Logical, matches.Count)) { break; }
            operation.EnsureActive();
            CollectMatches(plan.Logical, new DocumentEvaluationContext(document), matches, evaluator, operation, cancellationToken);
        }
        return ExecuteRows(plan.Logical, matches, evaluator, cancellationToken);
    }

    private static void CollectMatches(DocumentLogicalPlan logical, DocumentEvaluationContext source,
        List<DocumentEvaluationContext> matches, DocumentExpressionEvaluator evaluator, DocumentOperation operation, CancellationToken cancellationToken)
    {
        if (logical.Query.Unnest.Count == 0)
        {
            if (evaluator.Matches(logical.Query.Predicate, source)) { matches.Add(source); }
            return;
        }
        using var rows = Expand(logical.Query, source, evaluator, operation, cancellationToken).GetEnumerator();
        // Test the completed window before advancing, including within one source document.
        while (!PageComplete(logical, matches.Count) && rows.MoveNext())
        {
            if (evaluator.Matches(logical.Query.Predicate, rows.Current)) { matches.Add(rows.Current); }
        }
    }

    // Internal so tests can cancel from reads of borrowed JSON during expansion without a timer.
    internal static IEnumerable<DocumentEvaluationContext> Expand(OqlSelectExpression query, DocumentEvaluationContext source,
        DocumentExpressionEvaluator evaluator, DocumentOperation operation, CancellationToken cancellationToken)
    {
        var pending = new Stack<(DocumentEvaluationContext Row, int Next, JsonElement.ArrayEnumerator Items)>();
        var row = source;
        int next = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            if (next == query.Unnest.Count) { yield return row; }
            else
            {
                var binding = query.Unnest[next];
                var value = evaluator.Evaluate(binding.Collection, row);
                if (value is JsonElement { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0)
                {
                    var items = array.EnumerateArray();
                    items.MoveNext();
                    pending.Push((row, next + 1, items));
                    row = row.Bind(binding.Variable, items.Current);
                    next++;
                    continue;
                }
                if (binding.IsLeft)
                {
                    row = row.Bind(binding.Variable, DocumentMissing.Instance);
                    next++;
                    continue;
                }
            }
            bool resumed = false;
            while (pending.TryPop(out var frame))
            {
                var items = frame.Items;
                if (!items.MoveNext()) { continue; }
                pending.Push((frame.Row, frame.Next, items));
                row = frame.Row.Bind(query.Unnest[frame.Next - 1].Variable, items.Current);
                next = frame.Next;
                resumed = true;
                break;
            }
            if (!resumed) { yield break; }
        }
    }

    // Use the planner's fixed result types, including function, operator and META results.
    // Synthetic UNNEST stars always produce objects; plain stars can have scalar roots.
    private static bool PageComplete(DocumentLogicalPlan logical, int matched)
        => logical.CanStopEarly &&
            logical.Limit is long limit && Math.Max(0L, matched - logical.Offset) >= limit;

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
                row.OrderKeys = query.OrderBy.Select((order, index) => query.IsValue && logical.ProjectedOrdering[index]
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
            var columnType = types.Length == 0 ? logical.Projections[i].Type : types.Length == 1 ? types[0] :
                types.All(kind => kind is DatabaseType.Int64 or DatabaseType.Decimal) ? DatabaseType.Decimal : DatabaseType.Null;
            // JSON has one number kind. An Int64 header is the evidence the client uses
            // to recover long values; other numeric cells use the decimal scalar domain.
            if (columnType != DatabaseType.Int64 && types.Contains(DatabaseType.Int64))
            {
                foreach (var row in output)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (row.Values[i] is long integer) { row.Values[i] = (decimal)integer; }
                }
            }
            columns[i] = new DatabaseColumn { Name = logical.Projections[i].Name, Ordinal = i, Type = columnType };
        }
        // Infer and normalize column types over the complete result before slicing.
        int start = (int)Math.Min(logical.Offset, output.Count);
        int take = (int)Math.Min(logical.Limit ?? long.MaxValue, output.Count - start);
        if (start != 0 || take != output.Count) { output = output.GetRange(start, take); }
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
        if (plan.IfNotExists && database.Catalog.GetIndexes(plan.Collection.Id, operation.Context.Snapshot).Any(index => index.Name == plan.IndexName))
        { return DocumentCommandResult.Success; }
        await database.Catalog.CreateIndexAsync(plan.Collection.Id, plan.IndexName, plan.Paths, plan.IsUnique,
            operation.Context, cancellationToken).ConfigureAwait(false);
        return DocumentCommandResult.Success;
    }

    private static async ValueTask<DatabaseResult> ExecuteDropIndexAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentDropIndexPlan plan, CancellationToken cancellationToken)
    {
        await PrepareIndexChangeAsync(database, operation, plan.Collection, "DROP INDEX", cancellationToken, plan.IfExists ? plan.IndexName : null).ConfigureAwait(false);
        if (plan.IfExists && !database.Catalog.GetIndexes(plan.Collection.Id, operation.Context.Snapshot).Any(index => index.Name == plan.IndexName))
        { return DocumentCommandResult.Success; }
        await database.Catalog.DeleteIndexAsync(plan.Collection.Id, plan.IndexName,
            operation.Context, cancellationToken).ConfigureAwait(false);
        return DocumentCommandResult.Success;
    }

    private static async ValueTask PrepareIndexChangeAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentCollectionMetadata collection, string operationName, CancellationToken cancellationToken, string? absentNoOp = null)
    {
        await database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
        if (collection != database.Catalog.FindCollection(collection.Name, database.LatestSnapshot(operation.Context)))
        {
            DatabaseTransactionAbortedException.ThrowDocumentConflict();
        }
        if (collection.Owner == DatabaseObjectOwner.Schema &&
            (absentNoOp is null || database.Catalog.GetIndexes(collection.Id, operation.Context.Snapshot).Any(index => index.Name == absentNoOp)))
        { throw new DatabaseObjectLockedException(collection.Name, collection.OwningSchema!, operationName); }
        if (!database.Catalog.GetDocuments(collection.Id, null, operation.Context.Snapshot)
                .SequenceEqual(database.Catalog.GetDocuments(collection.Id, null, database.LatestSnapshot(operation.Context))) ||
            !database.Catalog.GetIndexes(collection.Id, operation.Context.Snapshot)
                .SequenceEqual(database.Catalog.GetIndexes(collection.Id, database.LatestSnapshot(operation.Context))))
        {
            DatabaseTransactionAbortedException.ThrowDocumentConflict();
        }
    }

    private static DatabaseType GetType(object? value) => value switch
    {
        bool => DatabaseType.Boolean,
        long => DatabaseType.Int64,
        decimal => DatabaseType.Decimal,
        string => DatabaseType.String,
        JsonElement => DatabaseType.Json,
        _ => DatabaseType.Null,
    };

    [global::System.Diagnostics.DebuggerDisplay("EvaluatedRow")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanExecutor.EvaluatedRow.DebugView))]
    private sealed record EvaluatedRow(object?[] Values, DocumentEvaluationContext Document, IReadOnlyList<DocumentEvaluationContext>? Group, int Ordinal)
    {
        internal object?[] OrderKeys { get; set; } = [];

        private sealed class DebugView
        {
            private readonly global::Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanExecutor.EvaluatedRow _target;

            public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanExecutor.EvaluatedRow value)
            {
                _target = value;
            }

            public object?[] Values => _target.Values;
            public global::Assimalign.Cohesion.Database.Documents.Internal.DocumentEvaluationContext Document => _target.Document;
            public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Internal.DocumentEvaluationContext>? Group => _target.Group;
            public int Ordinal => _target.Ordinal;
        }
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
