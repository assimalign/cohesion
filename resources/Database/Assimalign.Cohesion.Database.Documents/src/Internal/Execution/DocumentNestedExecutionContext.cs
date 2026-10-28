using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// Statement-local owner. Future correlation passes the caller's binding frame through
// EvaluateAsync; it never opens another operation or transaction.
[System.Diagnostics.DebuggerDisplay("DocumentNestedExecutionContext")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DocumentNestedExecutionContext.DebugView))]
internal sealed class DocumentNestedExecutionContext : IDisposable
{
    private readonly DocumentDatabase _database;
    private readonly DocumentOperation _operation;
    private readonly IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan> _plans;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private readonly Dictionary<OqlSubqueryExpression, JsonElement> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly List<JsonElement> _values = [];
    private readonly DocumentAggregateMemory.Reservation _retained;

    internal long RetainedBytes => _retained.Bytes;

    internal DocumentNestedExecutionContext(DocumentDatabase database, DocumentOperation operation,
        IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan> plans,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        _database = database;
        _operation = operation;
        _plans = plans;
        _parameters = parameters;
        _retained = operation.GetStatementMemory(cancellationToken).Reserve();
    }

    internal async ValueTask<object?> EvaluateAsync(OqlSubqueryExpression expression,
        DocumentEvaluationContext outer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operation.EnsureActive();
        var plan = _plans[expression];
        if (plan.CacheForStatement && _cache.TryGetValue(expression, out var cached)) { return cached; }
        // The binding frame is intentionally explicit even while Q1 refuses correlation.
        _ = outer;
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
            else if (expression.Query.Projections.Count == 1 && expression.Query.Projections[0].Expression is OqlStarExpression &&
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
        _retained.Charge(bytes);
        JsonElement array;
        try { array = DocumentJsonValue.Array(values); }
        catch { _retained.Rewind(_retained.Bytes - bytes); throw; }
        long actual = 128L + memory.ValueBytes(array);
        if (actual > bytes) { _retained.Charge(actual - bytes); }
        else { _retained.Rewind(_retained.Bytes - bytes + actual); }
        _values.Add(array);
        if (plan.CacheForStatement) { _cache.Add(expression, array); }
        return array;
    }

    public void Dispose()
    {
        _cache.Clear();
        _values.Clear();
        _retained.Dispose();
    }

    private sealed class DebugView
    {
        public DebugView(DocumentNestedExecutionContext value) { _ = value; }
    }
}
