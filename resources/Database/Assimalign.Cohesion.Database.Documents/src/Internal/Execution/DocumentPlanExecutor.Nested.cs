using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    internal static ValueTask<DatabaseResult> ExecuteNestedQueryAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentStatementPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken,
        DocumentNestedExecutionContext nested, string? writeVerb = null)
    {
        var logical = SourceLogical(plan);
        if (logical.Limit == 0) { return ValueTask.FromResult<DatabaseResult>(CreateResult(logical, [], cancellationToken)); }
        return plan switch
        {
            DocumentFromPlan source => ExecuteFromAsync(database, operation, source, parameters, cancellationToken, nested, writeVerb),
            DocumentPlan select => ExecuteNestedSelectAsync(database, operation, select, parameters, cancellationToken, writeVerb, nested),
            DocumentSystemCollectionPlan system => ExecuteNestedSystemCollectionAsync(database, operation, system, parameters, cancellationToken, writeVerb, nested),
            _ => throw new DocumentPlanningException("CD05001", ErrorMessages.PlanExecutorExecuteAsyncFailure),
        };
    }

    private static async ValueTask<DatabaseResult> ExecuteNestedStatementAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentNestedPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        using var nested = new DocumentNestedExecutionContext(database, operation, plan.Queries, parameters, cancellationToken);
        var memory = operation.GetStatementMemory(cancellationToken);
        long before = memory.Bytes - nested.RetainedBytes;
        try { return plan.Root switch
        {
            DocumentUpdatePlan update => await ExecuteUpdateAsync(database, operation, update, parameters, cancellationToken, nested).ConfigureAwait(false),
            DocumentDeletePlan delete => await ExecuteDeleteAsync(database, operation, delete, parameters, cancellationToken, nested).ConfigureAwait(false),
            DocumentInsertPlan insert => await ExecuteWriteAsync(database, operation, insert.Collection, insert.Statement, insert.Statement.Values, null, true, insert.Returning, parameters, cancellationToken, nested).ConfigureAwait(false),
            DocumentInsertSelectPlan insert => await ExecuteWriteAsync(database, operation, insert.Collection, insert.Statement, insert.Statement.Values, (insert.Source, insert.KeyOrdinal, insert.ValueOrdinal), true, insert.Returning, parameters, cancellationToken, nested).ConfigureAwait(false),
            DocumentUpsertPlan upsert => await ExecuteWriteAsync(database, operation, upsert.Collection, upsert.Statement, upsert.Statement.Values,
                upsert.Source is { } source ? (source, upsert.KeyOrdinal, upsert.ValueOrdinal) : null, false, upsert.Returning, parameters, cancellationToken, nested).ConfigureAwait(false),
            _ => await ExecuteNestedQueryAsync(database, operation, plan.Root, parameters, cancellationToken, nested).ConfigureAwait(false),
        }; }
        catch { memory.Release(memory.Bytes - nested.RetainedBytes - before); throw; }
    }

    private static async ValueTask<DatabaseResult> ExecuteNestedSelectAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, string? writeVerb, DocumentNestedExecutionContext nested)
    {
        var query = plan.Logical.Query;
        var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters, cancellationToken, writeVerb, operation.TransactionStart,
            query.Unnest.Count > 0 ? query.Alias ?? query.Collection : null, database.Engine.Functions, database.Name, operation: operation, nested: nested, sourceName: query.Alias ?? query.Collection);
        if (plan.Access is DocumentOrderingPath { Consumed: { } consumed })
        { return await ExecuteNestedOrderedSelectAsync(database, operation, plan, consumed, evaluator, cancellationToken, nested).ConfigureAwait(false); }
        var candidates = await ReadCandidatesAsync(database, operation, plan.Collection, plan.Access is DocumentOrderingPath ordering ? ordering.Input : plan.Access, cancellationToken).ConfigureAwait(false);

        using var sourceMemory = evaluator.Memory.Reserve();
        // Catalog identities and the ordinal sort's staging arrays remain live with
        // the decoded source. Charge before cloning the first retained document.
        sourceMemory.Charge(128L + candidates.Count * 128L);
        foreach (var entry in candidates) { sourceMemory.Retain(entry.Id); }
        var matches = new List<DocumentEvaluationContext>();
        // Identity is the baseline and sort tie break. A bounded identity DESC
        // page must visit candidates in reverse identity order before filtering.
        var ordered = plan.Logical.CanStopEarly && query.OrderBy.Count == 1 && query.OrderBy[0].Descending
            ? candidates.OrderByDescending(entry => entry.Id, StringComparer.Ordinal)
            : candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal);
        foreach (var entry in ordered)
        {
            if (PageComplete(plan.Logical, matches.Count)) { break; }
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            var document = nested.ReadDocument(entry);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            var context = new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version,
                query.Unnest.Count == 0 && query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression ? document.Content : default);
            if (query.Unnest.Count == 0)
            {
                if ((await evaluator.MatchesAsync(query.Predicate, context, null, nested).ConfigureAwait(false)))
                {
                    RetainSource(context, sourceMemory, evaluator.Memory);
                    sourceMemory.Charge(128);
                    matches.Add(context with { Content = json.RootElement.Clone() });
                }
            }
            else
            {
                long before = sourceMemory.Bytes;
                int previousCount = matches.Count;
                RetainSource(context, sourceMemory, evaluator.Memory);
                await CollectNestedMatchesAsync(plan.Logical, context with { Content = json.RootElement.Clone() }, matches, evaluator, operation, cancellationToken, sourceMemory, nested).ConfigureAwait(false);
                if (matches.Count == previousCount) { sourceMemory.Rewind(before); }
            }
        }

        return (await ExecuteNestedRowsAsync(plan.Logical, matches, evaluator, cancellationToken, ordered: false, nested).ConfigureAwait(false));
    }

    private static async ValueTask<DatabaseResult> ExecuteNestedSystemCollectionAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentSystemCollectionPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, string? writeVerb, DocumentNestedExecutionContext nested)
    {
        var query = plan.Logical.Query;
        var evaluator = new DocumentExpressionEvaluator(query.Alias, parameters, cancellationToken, writeVerb, operation.TransactionStart,
            query.Unnest.Count > 0 ? query.Alias ?? query.Collection : null, database.Engine.Functions, database.Name, operation: operation, nested: nested, sourceName: query.Alias ?? query.Collection);
        var matches = new List<DocumentEvaluationContext>();
        using var sourceMemory = evaluator.Memory.Reserve();
        foreach (var document in DocumentSystemCollections.Enumerate(database, operation.Context.Snapshot, plan.Name, cancellationToken))
        {
            if (PageComplete(plan.Logical, matches.Count)) { break; }
            operation.EnsureActive();
            long before = sourceMemory.Bytes;
            int previousCount = matches.Count;
            RetainSource(new DocumentEvaluationContext(document), sourceMemory, evaluator.Memory);
            await CollectNestedMatchesAsync(plan.Logical, new DocumentEvaluationContext(document), matches, evaluator, operation, cancellationToken, sourceMemory, nested).ConfigureAwait(false);
            if (matches.Count == previousCount) { sourceMemory.Rewind(before); }
        }
        return (await ExecuteNestedRowsAsync(plan.Logical, matches, evaluator, cancellationToken, ordered: false, nested).ConfigureAwait(false));
    }

    private static async ValueTask CollectNestedMatchesAsync(DocumentLogicalPlan logical, DocumentEvaluationContext source,
        List<DocumentEvaluationContext> matches, DocumentExpressionEvaluator evaluator, DocumentOperation operation, CancellationToken cancellationToken,
        DocumentAggregateMemory.Reservation sourceMemory, DocumentNestedExecutionContext nested)
    {
        if (logical.Query.Unnest.Count == 0)
        {
            if ((await evaluator.MatchesAsync(logical.Query.Predicate, source, null, nested).ConfigureAwait(false))) { sourceMemory.Charge(128); matches.Add(source); }
            return;
        }
        await using var rows = ExpandNestedAsync(logical.Query, source, evaluator, operation, cancellationToken, nested).GetAsyncEnumerator(cancellationToken);
        // Test the completed window before advancing, including within one source document.
        while (!PageComplete(logical, matches.Count) && await rows.MoveNextAsync().ConfigureAwait(false))
        {
            if ((await evaluator.MatchesAsync(logical.Query.Predicate, rows.Current, null, nested).ConfigureAwait(false)))
            {
                sourceMemory.Charge(128L + logical.Query.Unnest.Count * 64L);
                var owned = rows.Current with { Bindings = null };
                var frames = new Stack<DocumentVariableBinding>();
                for (var frame = rows.Current.Bindings; frame is not null; frame = frame.Parent) { frames.Push(frame); }
                foreach (var frame in frames)
                {
                    sourceMemory.Retain(frame.Value);
                    owned = owned.Bind(frame.Name, frame.Value is JsonElement json ? json.Clone() : frame.Value);
                }
                matches.Add(owned);
            }
        }
    }

    private static async IAsyncEnumerable<DocumentEvaluationContext> ExpandNestedAsync(OqlSelectExpression query, DocumentEvaluationContext source,
        DocumentExpressionEvaluator evaluator, DocumentOperation operation, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, DocumentNestedExecutionContext nested)
    {
        await foreach (var row in ExpandSourceAsync(query, source, evaluator, operation, cancellationToken, nested).ConfigureAwait(false))
        { yield return row; }
    }

    private static async ValueTask<DatabaseResult> ExecuteNestedRowsAsync(DocumentLogicalPlan logical, List<DocumentEvaluationContext> matches,
        DocumentExpressionEvaluator evaluator, CancellationToken cancellationToken, bool ordered, DocumentNestedExecutionContext nested)
    {
        var memory = evaluator.Memory;
        long before = memory.Bytes - nested.RetainedBytes;
        try { return (await ExecuteNestedRowsCoreAsync(logical, matches, evaluator, cancellationToken, ordered, nested).ConfigureAwait(false)); }
        catch
        {
            memory.Release(memory.Bytes - nested.RetainedBytes - before);
            throw;
        }
    }

    private static async ValueTask<DatabaseResult> ExecuteNestedRowsCoreAsync(DocumentLogicalPlan logical, List<DocumentEvaluationContext> matches,
        DocumentExpressionEvaluator evaluator, CancellationToken cancellationToken, bool ordered, DocumentNestedExecutionContext nested)
    {
        var query = logical.Query;
        var memory = evaluator.Memory;
        using var groupMemory = memory.Reserve();
        var output = new List<EvaluatedRow>();
        if (logical.IsGrouped)
        {
            var groups = new SortedDictionary<object?[], List<DocumentEvaluationContext>>(TupleComparer.Instance);
            // Alias frames share key representations, including repeated JSON views.
            // Track ownership only on this path so ordinary grouping keeps its allocation shape.
            var retainedValues = query.GroupAliases.Any(name => name is not null)
                ? new HashSet<object>(GroupValueComparer.Instance) : null;
            groupMemory.Charge(256);
            if (retainedValues is not null) { groupMemory.Charge(128); }
            if (query.GroupBy.Count == 0) { groupMemory.Charge(128); groups.Add([], matches); }
            else
            {
                foreach (var document in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = new object?[query.GroupBy.Count];
                    long scratchStart = memory.Bytes - nested.RetainedBytes;
                    try
                    {
                        for (int index = 0; index < key.Length; index++)
                        {
                            var value = await evaluator.EvaluateAsync(query.GroupBy[index], document, null, nested).ConfigureAwait(false);
                            key[index] = value is JsonElement json ? json.Clone() : value;
                        }
                    }
                    finally { memory.Release(memory.Bytes - nested.RetainedBytes - scratchStart); }
                    if (!groups.TryGetValue(key, out var group))
                    {
                        groupMemory.Charge(192L + key.Length * 16L);
                        foreach (var value in key) { groupMemory.Charge(GroupValueBytes(value, retainedValues, memory)); }
                        groups.Add(key, group = []);
                    }
                    groupMemory.Charge(128);
                    var member = document;
                    for (int index = 0; index < query.GroupAliases.Count; index++)
                    {
                        if (query.GroupAliases[index] is not string name) { continue; }
                        groupMemory.Charge(64);
                        groupMemory.Charge(GroupValueBytes(key[index], retainedValues, memory));
                        member = member.Bind(name, key[index]);
                    }
                    group.Add(member);
                }
            }
            foreach (var group in groups.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = group.Count > 0 ? group[0] : default;
                long wrapperStart = groupMemory.Bytes;
                if (query.GroupAs is string groupAs)
                {
                    try
                    {
                        var array = GroupArray(query, group, memory, cancellationToken);
                        // Passing the ordinary group reservation into a materializer would
                        // make it escape, adding 32 bytes even to queries without GROUP AS.
                        groupMemory.Adopt(array.RetainedBytes);
                        document = document.Bind(groupAs, array.Value);
                    }
                    catch (DocumentPlanningException error) when (error.Diagnostics[0].Start is null && query.Location is not null)
                    { throw error.At(query.Location); }
                }
                long before = memory.Bytes - nested.RetainedBytes;
                bool matchesHaving;
                try { matchesHaving = (await evaluator.MatchesAsync(query.Having, document, group, nested).ConfigureAwait(false)); }
                finally { memory.Release(memory.Bytes - nested.RetainedBytes - before); }
                if (matchesHaving) { await ProjectAsync(document, group).ConfigureAwait(false); }
                else { groupMemory.Rewind(wrapperStart); }
            }
        }
        else
        {
            foreach (var document in matches) { cancellationToken.ThrowIfCancellationRequested(); await ProjectAsync(document, null).ConfigureAwait(false); }
        }
        if (query.IsDistinct)
        {
            var seen = new SortedSet<object?[]>(TupleComparer.Instance);
            var distinct = new List<EvaluatedRow>();
            foreach (var row in output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(row.Values)) { distinct.Add(row); }
                else { memory.Release(row.RetainedBytes); }
            }
            output = distinct;
        }
        if (!ordered && query.OrderBy.Count > 0)
        {
            long orderBytes = 0;
            try
            {
                foreach (var row in output)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var aliases = memory.Reserve();
                    var previousBindings = nested.ProjectionBindings;
                    var previousBase = nested.ProjectionBase;
                    var projectionBindings = (DocumentVariableBinding?)null;
                    for (int index = 0; index < query.Projections.Count; index++)
                    {
                        if (query.Projections[index].Alias is not string name) { continue; }
                        aliases.Charge(64);
                        projectionBindings = new DocumentVariableBinding(name, row.Values[index], projectionBindings);
                    }
                    nested.ProjectionBindings = projectionBindings;
                    nested.ProjectionBase = row.Document.Bindings;
                    long before = memory.Bytes - nested.RetainedBytes;
                    try
                    {
                        row.OrderKeys = new object?[query.OrderBy.Count];
                        for (int index = 0; index < row.OrderKeys.Length; index++)
                        { row.OrderKeys[index] = query.IsValue && logical.ProjectedOrdering[index] ? row.Values[0] :
                            await evaluator.EvaluateAsync(query.OrderBy[index].Expression, row.Document, row.Group, nested).ConfigureAwait(false); }
                    }
                    finally
                    {
                        nested.ProjectionBindings = previousBindings;
                        nested.ProjectionBase = previousBase;
                        memory.Release(memory.Bytes - nested.RetainedBytes - before);
                    }
                    memory.Charge(32L + row.OrderKeys.Length * 16L);
                    orderBytes += 32L + row.OrderKeys.Length * 16L;
                    for (int index = 0; index < row.OrderKeys.Length; index++)
                    {
                        if (row.OrderKeys[index] is JsonElement json) { row.OrderKeys[index] = json.Clone(); }
                        orderBytes += memory.Retain(row.OrderKeys[index]);
                    }
                }
                output.Sort((left, right) =>
                {
                    for (int i = 0; i < query.OrderBy.Count; i++)
                    {
                        var order = query.OrderBy[i];
                        int comparison = DocumentExpressionEvaluator.CompareOrdered(left.OrderKeys[i], right.OrderKeys[i], order);
                        if (comparison != 0) { return comparison; }
                    }
                    return left.Ordinal.CompareTo(right.Ordinal);
                });
            }
            finally
            {
                foreach (var row in output) { row.OrderKeys = []; }
                memory.Release(orderBytes);
            }
        }
        return CreateResult(logical, output, cancellationToken, memory: memory);

        async ValueTask ProjectAsync(DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
        {
            long before = memory.Bytes - nested.RetainedBytes;
            object?[] values;
            try
            {
                values = new object?[logical.Projections.Count];
                for (int index = 0; index < values.Length; index++)
                { values[index] = await evaluator.EvaluateProjectionAsync(logical.Projections[index].Expression, document, group, DocumentRowWriter.WritesBareValue(query), nested).ConfigureAwait(false); }
            }
            finally { memory.Release(memory.Bytes - nested.RetainedBytes - before); }
            if (query.IsValue && values[0] is DocumentMissing) { values[0] = null; }
            for (int i = 0; i < values.Length; i++)
            { if (values[i] is JsonElement json) { values[i] = json.Clone(); } }
            long bytes = 192L + values.Length * 32L;
            memory.Charge(bytes);
            if (query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression)
            { memory.Charge(document.RawContent.Length); bytes += document.RawContent.Length; }
            foreach (var value in values) { bytes += memory.Retain(value); }
            output.Add(new EvaluatedRow(values, document, group, output.Count) { RetainedBytes = bytes });
        }
    }

    private static async ValueTask<DatabaseResult> ExecuteNestedOrderedSelectAsync(DocumentDatabase database,
        DocumentOperation operation, DocumentPlan plan, DocumentOrderedIndexCandidate consumed,
        DocumentExpressionEvaluator evaluator, CancellationToken cancellationToken, DocumentNestedExecutionContext nested)
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
        if (PageComplete(logical, 0)) { return await ExecuteNestedRowsAsync(logical, matches, evaluator, cancellationToken, ordered: true, nested).ConfigureAwait(false); }
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
                await ConsumeBucketAsync().ConfigureAwait(false);
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
        if (!PageComplete(logical, matches.Count)) { await ConsumeBucketAsync().ConfigureAwait(false); }
        bucket.Clear();
        bucket.Capacity = 0;
        prefix = default;
        bucketMemory.Rewind(0);
        bucketStorage.Rewind(0);
        return await ExecuteNestedRowsAsync(logical, matches, evaluator, cancellationToken, ordered: true, nested).ConfigureAwait(false);

        async ValueTask ConsumeBucketAsync()
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
                    var document = nested.ReadDocument(entry);
                using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
                var context = new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version,
                    query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression ? document.Content : default);
                if (!await evaluator.MatchesAsync(query.Predicate, context, null, nested).ConfigureAwait(false)) { continue; }
                RetainSource(context, sourceMemory, evaluator.Memory);
                sourceMemory.Charge(128);
                matches.Add(context with { Content = json.RootElement.Clone() });
            }
        }
    }
}
