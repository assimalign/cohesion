using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    private static async ValueTask<DatabaseResult> ExecuteOrderedSelectAsync(DocumentDatabase database,
        DocumentOperation operation, DocumentPlan plan, DocumentOrderedIndexCandidate consumed,
        DocumentExpressionEvaluator evaluator, CancellationToken cancellationToken)
    {
        var logical = plan.Logical;
        var query = logical.Query;
        var seek = consumed.Path;
        var matches = new List<DocumentEvaluationContext>();
        using var sourceMemory = evaluator.Memory.Reserve();
        using var bucketMemory = evaluator.Memory.Reserve();
        using var bucketStorage = evaluator.Memory.Reserve();
        var bucket = new List<DocumentCatalogEntry>();
        ReadOnlyMemory<byte> prefix = default;
        // LIMIT 0 with fixed headers opens no cursor and reads no entries/content.
        if (PageComplete(logical, 0)) { return ExecuteRows(logical, matches, evaluator, cancellationToken, ordered: true); }
        await using var cursor = database.Catalog.ReadIndexInOrderAsync(seek.Index, seek.EqualityPrefix ?? [],
            seek.Lower?.Value, seek.Lower?.Inclusive ?? false, seek.Upper?.Value, seek.Upper?.Inclusive ?? false,
            consumed.Ordering.Reverse, operation.Context.Snapshot,
            () => operation.Session.LastQueryIndexEntriesRead++, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await cursor.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            var current = cursor.Current;
            if (bucket.Count > 0 && !current.Key.Encoded.Span.StartsWith(prefix.Span))
            {
                ConsumeBucket();
                bucket.Clear();
                prefix = default;
                bucketMemory.Rewind(0);
                if (PageComplete(logical, matches.Count)) { break; }
            }
            if (bucket.Count == 0)
            {
                prefix = DocumentIndexKeys.OrderPrefix(current.Key, consumed.Ordering.OrderPrefixKeys);
                // Retaining a prefix retains the cursor's complete key array.
                bucketMemory.Charge(64L + current.Key.Encoded.Length);
            }
            bucketMemory.Charge(192);
            bucketMemory.Retain(current.Document.Id);
            if (bucket.Count == bucket.Capacity)
            {
                int capacity = Math.Max(4, checked(bucket.Capacity * 2));
                // Keep the reusable backing array charged across smaller buckets.
                bucketStorage.Charge((capacity - bucket.Capacity) * 96L);
                bucket.Capacity = capacity;
            }
            bucket.Add(current.Document);
        }
        if (!PageComplete(logical, matches.Count)) { ConsumeBucket(); }
        bucket.Clear();
        bucket.Capacity = 0;
        prefix = default;
        bucketMemory.Rewind(0);
        bucketStorage.Rewind(0);
        return ExecuteRows(logical, matches, evaluator, cancellationToken, ordered: true);

        void ConsumeBucket()
        {
            // Physical duplicate order is entry-reference/writer, and reverse scans
            // reverse those ties. OQL always uses ascending document identity instead.
            bucket.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
            string? previous = null;
            foreach (var entry in bucket)
            {
                if (PageComplete(logical, matches.Count)) { break; }
                cancellationToken.ThrowIfCancellationRequested();
                operation.EnsureActive();
                if (entry.Id == previous) { continue; }
                previous = entry.Id;
                operation.Session.LastQueryDocumentsRead++;
                var document = database.ReadDocument(entry);
                using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
                var context = new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version,
                    query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression ? document.Content : default);
                if (!evaluator.Matches(query.Predicate, context)) { continue; }
                RetainSource(context, sourceMemory, evaluator.Memory);
                sourceMemory.Charge(128);
                matches.Add(context with { Content = json.RootElement.Clone() });
            }
        }
    }
}
