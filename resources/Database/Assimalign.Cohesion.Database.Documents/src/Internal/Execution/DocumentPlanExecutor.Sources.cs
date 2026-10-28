using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    private static DocumentExpressionEvaluator SourceEvaluator(DocumentDatabase database, DocumentOperation operation,
        DocumentLogicalPlan logical, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var query = logical.Query;
        return new DocumentExpressionEvaluator(query.Alias, parameters, cancellationToken, transactionStart: operation.TransactionStart,
            starSourceName: query.Source is not null || query.Unnest.Count > 0 ? query.Alias ?? query.Collection : null,
            functions: database.Engine.Functions, databaseName: database.Name, operation: operation);
    }

    private static DocumentLogicalPlan SourceLogical(DocumentStatementPlan plan) => plan switch
    {
        DocumentPlan select => select.Logical,
        DocumentFromPlan source => source.Logical,
        DocumentSystemCollectionPlan system => system.Logical,
        _ => throw new DocumentPlanningException("CD05001", ErrorMessages.PlanExecutorExecuteAsyncFailure),
    };

    private static bool Streams(DocumentLogicalPlan logical)
        => !logical.IsGrouped && !logical.Query.IsDistinct && logical.Query.OrderBy.Count == 0;

    private static async ValueTask<DatabaseResult> ExecuteFromAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentFromPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken,
        DocumentNestedExecutionContext nested)
    {
        var logical = plan.Logical;
        var evaluator = SourceEvaluator(database, operation, logical, parameters, cancellationToken);
        var memory = evaluator.Memory;
        if (!Streams(logical))
        {
            // Grouping and ordering re-read their inputs. This is the only source-row
            // materialization, and its lifetime is bounded by the statement reservation.
            using var retained = memory.Reserve();
            var matches = new List<DocumentEvaluationContext>();
            await foreach (var row in SourceMatchesAsync(database, operation, plan, evaluator, parameters, nested, cancellationToken).ConfigureAwait(false))
            {
                retained.Charge(128L + logical.Query.Unnest.Count * 64L);
                retained.Retain(row.Content);
                // The input iterator lends its JSON until the next MoveNext. Cloning
                // here gives blocking stages an independent representation.
                var owned = new DocumentEvaluationContext(row.Content.Clone());
                var bindings = new Stack<DocumentVariableBinding>();
                for (var frame = row.Bindings; frame is not null; frame = frame.Parent) { bindings.Push(frame); }
                foreach (var frame in bindings)
                {
                    retained.Retain(frame.Value);
                    owned = owned.Bind(frame.Name, frame.Value is JsonElement json ? json.Clone() : frame.Value);
                }
                matches.Add(owned);
            }
            return await ExecuteNestedRowsAsync(logical, matches, evaluator, cancellationToken, false, nested).ConfigureAwait(false);
        }

        using var outputMemory = memory.Reserve();
        var output = new List<EvaluatedRow>();
        await foreach (var row in SourceMatchesAsync(database, operation, plan, evaluator, parameters, nested, cancellationToken).ConfigureAwait(false))
        {
            long before = outputMemory.Bytes;
            var values = await SourceProjectionAsync(logical, row, evaluator, nested, outputMemory).ConfigureAwait(false);
            output.Add(new EvaluatedRow(values, default, null, output.Count) { RetainedBytes = outputMemory.Bytes - before });
        }
        // The pull pipeline has already applied paging before projection.
        var result = CreateResult(logical with { Limit = null, Offset = 0 }, output, cancellationToken, memory: memory);
        outputMemory.Detach();
        return result;
    }

    private static async ValueTask<object?[]> SourceProjectionAsync(DocumentLogicalPlan logical, DocumentEvaluationContext row,
        DocumentExpressionEvaluator evaluator, DocumentNestedExecutionContext nested, DocumentAggregateMemory.Reservation retained)
    {
        var memory = evaluator.Memory;
        retained.Charge(192L + logical.Projections.Count * 32L);
        var values = new object?[logical.Projections.Count];
        for (int index = 0; index < values.Length; index++)
        {
            long before = memory.Bytes - nested.RetainedBytes;
            object? value;
            try
            {
                value = await evaluator.EvaluateProjectionAsync(logical.Projections[index].Expression, row, null,
                    DocumentRowWriter.WritesBareValue(logical.Query), nested).ConfigureAwait(false);
            }
            finally { memory.Release(memory.Bytes - nested.RetainedBytes - before); }
            if (logical.Query.IsValue && value is DocumentMissing) { value = null; }
            retained.Retain(value);
            values[index] = value is JsonElement json ? json.Clone() : value;
        }
        return values;
    }

    // Every yielded context is borrowed until the next MoveNext. Explicit enumerators
    // let LIMIT test its window before pulling even one additional inner row.
    private static async IAsyncEnumerable<DocumentEvaluationContext> SourceMatchesAsync(DocumentDatabase database,
        DocumentOperation operation, DocumentStatementPlan plan, DocumentExpressionEvaluator evaluator,
        IReadOnlyDictionary<string, object?>? parameters, DocumentNestedExecutionContext nested,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var logical = SourceLogical(plan);
        bool paged = Streams(logical);
        long seen = 0, emitted = 0;
        await using var input = SourceInputAsync(database, operation, plan, evaluator, parameters, nested, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while ((!paged || logical.Limit is null || emitted < logical.Limit) && await input.MoveNextAsync().ConfigureAwait(false))
        {
            await using var expanded = ExpandSourceAsync(logical.Query, input.Current, evaluator, operation, cancellationToken, nested)
                .GetAsyncEnumerator(cancellationToken);
            while ((!paged || logical.Limit is null || emitted < logical.Limit) && await expanded.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation.EnsureActive();
                long before = evaluator.Memory.Bytes - nested.RetainedBytes;
                bool matches;
                try { matches = await evaluator.MatchesAsync(logical.Query.Predicate, expanded.Current, null, nested).ConfigureAwait(false); }
                finally { evaluator.Memory.Release(evaluator.Memory.Bytes - nested.RetainedBytes - before); }
                if (!matches || paged && seen++ < logical.Offset) { continue; }
                emitted++;
                yield return expanded.Current;
            }
        }
    }

    private static async IAsyncEnumerable<DocumentEvaluationContext> ExpandSourceAsync(OqlSelectExpression query,
        DocumentEvaluationContext source, DocumentExpressionEvaluator evaluator, DocumentOperation operation,
        [EnumeratorCancellation] CancellationToken cancellationToken, DocumentNestedExecutionContext nested)
    {
        if (query.Unnest.Count == 0)
        { cancellationToken.ThrowIfCancellationRequested(); operation.EnsureActive(); yield return source; yield break; }
        using var retained = evaluator.Memory.Reserve();
        var pending = new Stack<(DocumentEvaluationContext Row, int Next, JsonElement.ArrayEnumerator Items, long Start)>();
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
                long start = retained.Bytes;
                long before = evaluator.Memory.Bytes - nested.RetainedBytes;
                object? value;
                try { value = await evaluator.EvaluateAsync(binding.Collection, row, null, nested).ConfigureAwait(false); }
                finally { evaluator.Memory.Release(evaluator.Memory.Bytes - nested.RetainedBytes - before); }
                if (value is JsonElement { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0)
                {
                    // Path views borrow the current input or an active parent frame;
                    // nested arrays have the Q1 statement owner. Other expressions
                    // may create independent arrays that this cursor must retain.
                    if (binding.Collection is not (OqlPathExpression or OqlSubqueryExpression)) { retained.Retain(value); }
                    var items = array.EnumerateArray();
                    items.MoveNext();
                    pending.Push((row, next + 1, items, start));
                    row = row.Bind(binding.Variable, items.Current);
                    next++;
                    continue;
                }
                if (binding.IsLeft) { row = row.Bind(binding.Variable, DocumentMissing.Instance); next++; continue; }
            }
            bool resumed = false;
            while (pending.TryPop(out var frame))
            {
                var items = frame.Items;
                if (!items.MoveNext()) { retained.Rewind(frame.Start); continue; }
                pending.Push((frame.Row, frame.Next, items, frame.Start));
                row = frame.Row.Bind(query.Unnest[frame.Next - 1].Variable, items.Current);
                next = frame.Next;
                resumed = true;
                break;
            }
            if (!resumed) { yield break; }
        }
    }

    private static async IAsyncEnumerable<DocumentEvaluationContext> SourceInputAsync(DocumentDatabase database,
        DocumentOperation operation, DocumentStatementPlan plan, DocumentExpressionEvaluator evaluator,
        IReadOnlyDictionary<string, object?>? parameters, DocumentNestedExecutionContext nested,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (plan is DocumentFromPlan { Input: not null } derived)
        {
            await foreach (var row in StreamSourceRowsAsync(database, operation, derived.Input, parameters, nested, cancellationToken).ConfigureAwait(false))
            { yield return new DocumentEvaluationContext(row); }
            yield break;
        }
        if (plan is DocumentFromPlan expression)
        {
            var memory = evaluator.Memory;
            long before = memory.Bytes - nested.RetainedBytes;
            object? value;
            try { value = await evaluator.EvaluateAsync(expression.Logical.Query.Source!, default, null, nested).ConfigureAwait(false); }
            finally { memory.Release(memory.Bytes - nested.RetainedBytes - before); }
            using var retained = memory.Reserve();
            retained.Retain(value);
            // Couchbase ExpressionScan: arrays expand, MISSING has no rows; every
            // other JSON value, including NULL and objects, contributes one row.
            if (value is DocumentMissing) { yield break; }
            if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                { cancellationToken.ThrowIfCancellationRequested(); yield return new DocumentEvaluationContext(item); }
            }
            else
            {
                if (value is not JsonElement) { retained.Charge(384L + 3L * memory.ValueBytes(value)); }
                yield return new DocumentEvaluationContext(JsonValue(value));
            }
            yield break;
        }
        if (plan is DocumentSystemCollectionPlan system)
        {
            foreach (var row in DocumentSystemCollections.Enumerate(database, operation.Context.Snapshot, system.Name, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation.EnsureActive();
                using var rowMemory = evaluator.Memory.Reserve();
                rowMemory.Retain(row);
                yield return new DocumentEvaluationContext(row);
            }
            yield break;
        }
        var select = (DocumentPlan)plan;
        var access = select.Access is DocumentOrderingPath ordering ? ordering.Input : select.Access;
        var candidates = await ReadCandidatesAsync(database, operation, select.Collection, access, cancellationToken).ConfigureAwait(false);
        using var candidatesMemory = evaluator.Memory.Reserve();
        candidatesMemory.Charge(128L + candidates.Count * 128L);
        foreach (var entry in candidates) { candidatesMemory.Retain(entry.Id); }
        foreach (var entry in candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            operation.Session.LastQueryDocumentsRead++;
            var document = database.ReadDocument(entry);
            using var rowMemory = evaluator.Memory.Reserve();
            rowMemory.Charge(128L + document.Content.Length);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            rowMemory.Retain(json.RootElement);
            yield return new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version);
        }
    }

    private static async IAsyncEnumerable<JsonElement> StreamSourceRowsAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentStatementPlan plan, IReadOnlyDictionary<string, object?>? parameters, DocumentNestedExecutionContext nested,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var logical = SourceLogical(plan);
        var evaluator = SourceEvaluator(database, operation, logical, parameters, cancellationToken);
        if (!Streams(logical))
        {
            await using var result = (DatabaseResultSet)await ExecuteNestedQueryAsync(database, operation, plan, parameters,
                cancellationToken, nested).ConfigureAwait(false);
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new object?[result.Columns.Count];
                for (int index = 0; index < values.Length; index++)
                { values[index] = row.IsMissing(index) ? DocumentMissing.Instance : row.GetValue(index); }
                using var retained = evaluator.Memory.Reserve();
                retained.Charge(384L + values.Sum(value => 3L * evaluator.Memory.ValueBytes(value)));
                yield return SourceObject(logical, values);
            }
            yield break;
        }
        await foreach (var row in SourceMatchesAsync(database, operation, plan, evaluator, parameters, nested, cancellationToken).ConfigureAwait(false))
        {
            using var retained = evaluator.Memory.Reserve();
            var values = await SourceProjectionAsync(logical, row, evaluator, nested, retained).ConfigureAwait(false);
            retained.Charge(384L + values.Sum(value => 64L + 3L * evaluator.Memory.ValueBytes(value)));
            yield return SourceObject(logical, values);
        }
    }

    private static JsonElement SourceObject(DocumentLogicalPlan logical, object?[] values)
        => DocumentRowWriter.WritesBareValue(logical.Query) ? JsonValue(values[0]) :
            DocumentJsonValue.Object(logical.Projections.Select((projection, index) => new KeyValuePair<string, object?>(projection.Name, values[index])));

    private static JsonElement JsonValue(object? value)
        => value is JsonElement json ? json : DocumentJsonValue.Array([value])[0];
}
