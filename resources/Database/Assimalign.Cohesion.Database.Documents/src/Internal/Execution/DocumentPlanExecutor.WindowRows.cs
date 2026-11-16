using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    // Keep the ordinary row pipeline's closure and object layouts unchanged.
    private static async ValueTask<DatabaseResult> ExecuteWindowRowsAsync(DocumentLogicalPlan logical, List<DocumentEvaluationContext> matches,
        DocumentExpressionEvaluator evaluator, CancellationToken cancellationToken, bool ordered, DocumentNestedExecutionContext nested)
    {
        var query = logical.Query;
        var memory = evaluator.Memory;
        using var groupMemory = memory.Reserve();
        var output = new List<EvaluatedRow>();
        var windowRows = new List<EvaluatedRow>();
        using var windowMemory = memory.Reserve();
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
                if (matchesHaving) { Stage(document, group); }
                else { groupMemory.Rewind(wrapperStart); }
            }
        }
        else
        {
            foreach (var document in matches) { cancellationToken.ThrowIfCancellationRequested(); Stage(document, null); }
        }
        await ApplyWindowsAsync(query, windowRows, evaluator, nested, windowMemory, cancellationToken).ConfigureAwait(false);
        foreach (var row in windowRows) { await ProjectAsync(row.Document, row.Group).ConfigureAwait(false); }
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

        void Stage(DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
        {
            windowMemory.Charge(192);
            windowRows.Add(new EvaluatedRow([], document, group, windowRows.Count));
        }

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

}
