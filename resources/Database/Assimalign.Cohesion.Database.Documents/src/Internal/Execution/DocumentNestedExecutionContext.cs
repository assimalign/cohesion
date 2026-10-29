using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// Statement-local cache owner. Uncached arrays belong to the evaluating expression;
// consumers clone and charge retained values before releasing expression scratch.
[System.Diagnostics.DebuggerDisplay("DocumentNestedExecutionContext")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DocumentNestedExecutionContext.DebugView))]
internal sealed class DocumentNestedExecutionContext : IDisposable
{
    private readonly DocumentDatabase _database;
    private readonly DocumentOperation _operation;
    private readonly IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan> _plans;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private readonly Dictionary<OqlSubqueryExpression, JsonElement> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly DocumentAggregateMemory.Reservation _retained;
    private long _work;
    private readonly long _lookupLimit;
    private long _lookupBytes;
    private Dictionary<(Guid Collection, string Id), LinkedListNode<CachedLookup>>? _lookups;
    private LinkedList<CachedLookup>? _lookupOrder;

    internal long RetainedBytes => _retained.Bytes;
    internal DocumentVariableBinding? Bindings { get; private set; }
    internal DocumentVariableBinding? ProjectionBindings { get; set; }
    internal DocumentVariableBinding? ProjectionBase { get; set; }

    internal void ConsumeWork(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operation.EnsureActive();
        if (_work == _database.Engine.EngineOptions.StatementWorkBudget)
        { throw new DocumentPlanningException("CD05020", ErrorMessages.NestedWorkExceeded); }
        _work++;
    }

    internal DocumentEvaluationContext? Lookup(Guid collection, string id,
        DocumentAggregateMemory.Reservation active, CancellationToken cancellationToken)
    {
        ConsumeWork(cancellationToken);
        _lookups ??= new();
        _lookupOrder ??= new();
        if (_lookups.TryGetValue((collection, id), out var cached))
        {
            _lookupOrder.Remove(cached);
            _lookupOrder.AddLast(cached);
            RetainActive(cached.Value.Result, active);
            return cached.Value.Result;
        }
        var memory = _operation.GetStatementMemory(cancellationToken);
        using var scratch = memory.Reserve();
        scratch.Charge(256L + 2L * id.Length);
        _operation.Session.LastQueryKeyLookups++;
        var entry = _database.Catalog.FindDocument(collection, id, _operation.Context.Snapshot);
        DocumentEvaluationContext? result = null;
        if (entry is { } found)
        {
            scratch.Charge(384L + found.Length * 3L);
            var document = ReadDocument(found);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            scratch.Retain(json.RootElement);
            result = new DocumentEvaluationContext(json.RootElement.Clone(), found.Id, found.Version);
        }
        // Rows own a separate conservative reservation, even while the cache also
        // retains the representation. Eviction can therefore release only its owner.
        RetainActive(result, active);
        long bytes = scratch.Bytes;
        if (bytes <= _lookupLimit)
        {
            TrimLookups(bytes);
            _retained.Adopt(scratch.Detach());
            var node = _lookupOrder.AddLast(new CachedLookup((collection, id), result, bytes));
            _lookups.Add((collection, id), node);
            _lookupBytes += bytes;
        }
        return result;
    }

    internal JsonElement Nest(Guid collection, string id, DocumentEvaluationContext? match,
        DocumentAggregateMemory.Reservation active, CancellationToken cancellationToken)
    {
        ConsumeWork(cancellationToken);
        _lookups!.TryGetValue((collection, id), out var node);
        if (node?.Value.Nest is { } cached) { active.Retain(cached); return cached; }
        var memory = _operation.GetStatementMemory(cancellationToken);
        active.Charge(512L + (match is { } document ? 3L * memory.ValueBytes(document.Content) : 0));
        var array = DocumentJsonValue.Array(match is { } row ? [row.Content] : []);
        if (node is not null)
        {
            long bytes = 64L + memory.ValueBytes(array);
            if (bytes > _lookupLimit - node.Value.Bytes) { EvictLookup(node); }
            else
            {
                TrimLookups(bytes);
                _retained.Charge(bytes);
                _lookupBytes += bytes;
                node.Value.Bytes += bytes;
                node.Value.Nest = array;
            }
        }
        return array;
    }

    private static void RetainActive(DocumentEvaluationContext? result, DocumentAggregateMemory.Reservation active)
    {
        if (result is not { } row) { return; }
        active.Charge(128);
        active.Retain(row.Id);
        active.Retain(row.Content);
    }

    private void TrimLookups(long incoming)
    {
        while (_lookupBytes > _lookupLimit - incoming) { EvictLookup(_lookupOrder!.First!); }
    }

    private void EvictLookup(LinkedListNode<CachedLookup> node)
    {
        _lookups!.Remove(node.Value.Key);
        _lookupOrder!.Remove(node);
        _lookupBytes -= node.Value.Bytes;
        _retained.Rewind(_retained.Bytes - node.Value.Bytes);
    }

    internal Document ReadDocument(DocumentCatalogEntry entry)
    {
        _operation.Session.LastQueryDocumentsRead++;
        return _database.ReadDocument(entry);
    }

    internal DocumentNestedExecutionContext(DocumentDatabase database, DocumentOperation operation,
        IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan> plans,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        _database = database;
        _operation = operation;
        _plans = plans;
        _parameters = parameters;
        _retained = operation.GetStatementMemory(cancellationToken).Reserve();
        // Keep seven eighths of the grant available to sources, active rows and
        // blocking operators. Oversized entries are used without being cached.
        _lookupLimit = database.Engine.EngineOptions.StatementMemoryBudget / 8;
    }

    internal async ValueTask<object?> EvaluateAsync(OqlSubqueryExpression expression,
        DocumentVariableBinding? outer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operation.EnsureActive();
        var plan = _plans[expression];
        if (plan.CacheForStatement && _cache.TryGetValue(expression, out var cached)) { return cached; }
        var memory = _operation.GetStatementMemory(cancellationToken);
        long before = memory.Bytes - RetainedBytes;
        var previous = Bindings;
        var previousProjections = ProjectionBindings;
        var previousProjectionBase = ProjectionBase;
        JsonElement array;
        try
        {
            Bindings = outer;
            ProjectionBindings = null;
            ProjectionBase = null;
            array = await MaterializeAsync(expression, plan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Bindings = previous;
            ProjectionBindings = previousProjections;
            ProjectionBase = previousProjectionBase;
            memory.Release(memory.Bytes - RetainedBytes - before);
        }
        if (plan.CacheForStatement)
        {
            _retained.Charge(128L + memory.ValueBytes(array));
            _cache.Add(expression, array);
        }
        else { memory.Charge(128L + memory.ValueBytes(array)); }
        return array;
    }

    private async ValueTask<JsonElement> MaterializeAsync(OqlSubqueryExpression expression,
        DocumentNestedQueryPlan plan, CancellationToken cancellationToken)
    {
        _operation.Session.LastQuerySubqueriesExecuted++;
        await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteNestedQueryAsync(
            _database, _operation, plan.Plan, _parameters, cancellationToken, this).ConfigureAwait(false);
        var memory = _operation.GetStatementMemory(cancellationToken);
        using var scratch = memory.Reserve();
        var values = new List<object?>();
        scratch.Charge(128);
        await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? value;
            if (expression.Query.IsValue)
            { value = row.GetValue(0); }
            else if (expression.Query.Projections.Count == 1 && expression.Query.Projections[0].Expression is OqlUnaryExpression { Operator: ".*" })
            { value = row.IsMissing(0) ? DocumentMissing.Instance : row.GetValue(0); }
            else if (DocumentRowWriter.WritesBareValue(expression.Query) &&
                row.GetValue(0) is JsonElement { ValueKind: JsonValueKind.Object } document)
            { value = document; }
            else
            {
                var members = new List<KeyValuePair<string, object?>>();
                for (int index = 0; index < result.Columns.Count; index++)
                { members.Add(new(result.Columns[index].Name, row.IsMissing(index) ? DocumentMissing.Instance : row.GetValue(index))); }
                value = DocumentJsonValue.Object(members);
            }
            scratch.Retain(value);
            values.Add(value);
        }
        // Reserve the independent array before allocating its JSON representation.
        long bytes = 384L;
        foreach (var value in values) { bytes += 64L + 3L * memory.ValueBytes(value); }
        scratch.Charge(bytes);
        return DocumentJsonValue.Array(values);
    }

    public void Dispose()
    {
        _cache.Clear();
        _lookups?.Clear();
        _lookupOrder?.Clear();
        _lookupBytes = 0;
        _retained.Dispose();
    }

    private sealed class CachedLookup
    {
        public CachedLookup((Guid Collection, string Id) key, DocumentEvaluationContext? result, long bytes)
        { Key = key; Result = result; Bytes = bytes; }

        public (Guid Collection, string Id) Key { get; }
        public DocumentEvaluationContext? Result { get; }
        public long Bytes { get; set; }
        public JsonElement? Nest { get; set; }
    }

    private sealed class DebugView
    {
        public DebugView(DocumentNestedExecutionContext value) { _ = value; }
    }
}
