using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    private static async ValueTask<DatabaseResult> ExecuteSetOperationAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentSetOperationPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken,
        DocumentNestedExecutionContext? context = null)
    {
        using var owned = context is null ? new DocumentNestedExecutionContext(database, operation,
            new Dictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>(), parameters, cancellationToken) : null;
        var nested = context ?? owned!;
        var memory = operation.GetStatementMemory(cancellationToken);
        using var outputMemory = memory.Reserve();
        var output = new List<object?[]>();
        var types = plan.Columns.Select(column => column.Type).ToArray();
        bool staticTypes = SetTypesAreStatic(plan);
        bool paged = staticTypes && plan.Query.OrderBy.Count == 0;
        var mixed = new bool[types.Length];
        var observed = new bool[types.Length];
        if (!staticTypes) { Array.Fill(types, DatabaseType.Null); }
        long seen = 0;
        if (plan.Limit != 0)
        {
            await foreach (var row in StreamSetRowsAsync(database, operation, plan, parameters, nested, paged, false, cancellationToken).ConfigureAwait(false))
            {
                nested.ConsumeWork(cancellationToken);
                if (!staticTypes)
                {
                    for (int index = 0; index < types.Length; index++)
                    {
                        var next = GetType(row.Cells[index]);
                        if (next == DatabaseType.Null || mixed[index]) { continue; }
                        observed[index] = true;
                        if (types[index] == DatabaseType.Null) { types[index] = next; }
                        else if (types[index] != next)
                        {
                            if (types[index] is DatabaseType.Int64 or DatabaseType.Decimal && next is DatabaseType.Int64 or DatabaseType.Decimal)
                            { types[index] = DatabaseType.Decimal; }
                            else { mixed[index] = true; }
                        }
                    }
                }
                bool keep = paged || seen >= plan.Offset && (plan.Limit is null || seen - plan.Offset < plan.Limit);
                seen++;
                if (!keep) { continue; }
                outputMemory.Charge(192L + row.Cells.Length * 32L);
                var cells = new object?[row.Cells.Length];
                for (int index = 0; index < cells.Length; index++)
                {
                    outputMemory.Retain(row.Cells[index]);
                    cells[index] = row.Cells[index] is JsonElement json ? json.Clone() : row.Cells[index];
                }
                output.Add(cells);
            }
        }
        var columns = new DatabaseColumn[types.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            var type = mixed[index] ? DatabaseType.Null : !staticTypes && !observed[index] ? plan.Columns[index].Type : types[index];
            if (type != DatabaseType.Int64)
            {
                foreach (var cells in output)
                { cancellationToken.ThrowIfCancellationRequested(); if (cells[index] is long integer) { cells[index] = (decimal)integer; } }
            }
            columns[index] = new DatabaseColumn { Name = plan.Columns[index].Name, Ordinal = index, Type = type };
        }
        var result = new DocumentQueryResult(columns, output, plan.Shape != DocumentSetResultShape.Columns,
            memory: memory, retainedBytes: outputMemory.Bytes);
        outputMemory.Detach();
        return result;
    }

    // Dynamic types observe the pre-page stream while retaining only the selected page.
    private static bool SetTypesAreStatic(DocumentStatementPlan plan)
        => plan is DocumentSetOperationPlan compound ? compound.Shape == DocumentSetResultShape.Document ||
            SetTypesAreStatic(compound.Left) && SetTypesAreStatic(compound.Right) : SourceLogical(plan).Projections.All(projection =>
                projection.Type != DatabaseType.Null || projection.Expression is OqlLiteralExpression { Value: null or JsonElement { ValueKind: JsonValueKind.Null } } or OqlMissingExpression);

    private static async IAsyncEnumerable<SetRow> StreamSetRowsAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentSetOperationPlan plan, IReadOnlyDictionary<string, object?>? parameters, DocumentNestedExecutionContext nested,
        bool applyPage, bool needsValue, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (plan.Limit == 0) { yield break; }
        bool needsInputValue = needsValue || plan.Query.Operation != OqlSetOperation.Union || !plan.Query.IsAll || plan.Query.OrderBy.Count > 0 || plan.Shape == DocumentSetResultShape.Document;
        var memory = operation.GetStatementMemory(cancellationToken);
        using var distinctMemory = memory.Reserve();
        SortedSet<JsonElement>? seen = null;
        if (!plan.Query.IsAll)
        {
            distinctMemory.Charge(128);
            seen = new SortedSet<JsonElement>(Comparer<JsonElement>.Create((left, right) =>
            {
                nested.ConsumeWork(cancellationToken);
                return DocumentExpressionEvaluator.Compare(left, right, cancellationToken);
            }));
        }
        using var rightMemory = memory.Reserve();
        SortedDictionary<JsonElement, long>? rightCounts = null;
        if (plan.Query.Operation != OqlSetOperation.Union)
        {
            rightMemory.Charge(128);
            rightCounts = new SortedDictionary<JsonElement, long>(Comparer<JsonElement>.Create((left, right) =>
            {
                nested.ConsumeWork(cancellationToken);
                return DocumentExpressionEvaluator.Compare(left, right, cancellationToken);
            }));
            await foreach (var row in StreamQueryRowsAsync(database, operation, plan.Right, parameters, nested, true, cancellationToken).ConfigureAwait(false))
            {
                nested.ConsumeWork(cancellationToken);
                if (rightCounts.TryGetValue(row.Value, out long count))
                { if (plan.Query.IsAll) { rightCounts[row.Value] = checked(count + 1); } }
                else
                {
                    // Own only one equality key per distinct right value; occurrences are counters.
                    rightMemory.Charge(192L + memory.ValueBytes(row.Value));
                    rightCounts.Add(row.Value.Clone(), 1);
                }
            }
        }
        if (plan.Query.OrderBy.Count == 0)
        {
            long accepted = 0, emitted = 0;
            await using var input = InputAsync().GetAsyncEnumerator(cancellationToken);
            while ((!applyPage || plan.Limit is null || emitted < plan.Limit) && await input.MoveNextAsync().ConfigureAwait(false))
            {
                var row = input.Current;
                if (!Accept(row)) { continue; }
                if (applyPage && accepted++ < plan.Offset) { continue; }
                emitted++;
                yield return Shape(row);
            }
            yield break;
        }
        using var retained = memory.Reserve();
        var sorted = new List<(SetRow Row, object?[] Keys, int Ordinal)>();
        // ORDER BY binds only the result row. Async evaluation still shares statement work,
        // but the evaluator must not capture an enclosing query's bindings.
        var evaluator = new DocumentExpressionEvaluator(null, parameters, cancellationToken,
            transactionStart: operation.TransactionStart, functions: database.Engine.Functions,
            databaseName: database.Name, operation: operation);
        await foreach (var row in InputAsync().ConfigureAwait(false))
        {
            if (!Accept(row)) { continue; }
            retained.Charge(256L + row.Cells.Length * 32L + plan.Query.OrderBy.Count * 16L);
            retained.Retain(row.Value);
            var cells = new object?[row.Cells.Length];
            for (int index = 0; index < cells.Length; index++)
            { retained.Retain(row.Cells[index]); cells[index] = row.Cells[index] is JsonElement json ? json.Clone() : row.Cells[index]; }
            var value = row.Value.Clone();
            var keys = new object?[plan.Query.OrderBy.Count];
            for (int index = 0; index < keys.Length; index++)
            {
                long before = memory.Bytes - nested.RetainedBytes;
                object? key;
                try { key = await evaluator.EvaluateAsync(plan.Query.OrderBy[index].Expression, new DocumentEvaluationContext(value), null, nested).ConfigureAwait(false); }
                finally { memory.Release(memory.Bytes - nested.RetainedBytes - before); }
                retained.Retain(key);
                keys[index] = key is JsonElement json ? json.Clone() : key;
            }
            sorted.Add((new SetRow(value, cells), keys, sorted.Count));
        }
        // Array.Sort wraps comparer failures. Preserve the statement's coded work refusal.
        try
        {
            sorted.Sort((left, right) =>
            {
                nested.ConsumeWork(cancellationToken);
                for (int index = 0; index < plan.Query.OrderBy.Count; index++)
                {
                    int comparison = DocumentExpressionEvaluator.CompareOrdered(left.Keys[index], right.Keys[index], plan.Query.OrderBy[index]);
                    if (comparison != 0) { return comparison; }
                }
                return left.Ordinal.CompareTo(right.Ordinal);
            });
        }
        catch (InvalidOperationException error) when (error.InnerException is DocumentPlanningException or OperationCanceledException)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        long start = applyPage ? Math.Min(plan.Offset, sorted.Count) : 0;
        long end = applyPage ? start + Math.Min(plan.Limit ?? long.MaxValue, sorted.Count - start) : sorted.Count;
        for (int index = (int)start; index < end; index++)
        { cancellationToken.ThrowIfCancellationRequested(); yield return Shape(sorted[index].Row); }

        bool Accept(SetRow row)
        {
            nested.ConsumeWork(cancellationToken);
            if (rightCounts is not null)
            {
                bool present = rightCounts.TryGetValue(row.Value, out long count) && count > 0;
                if (present && plan.Query.IsAll) { rightCounts[row.Value] = count - 1; }
                if (present != (plan.Query.Operation == OqlSetOperation.Intersect)) { return false; }
            }
            if (seen is null) { return true; }
            if (!seen.Add(row.Value)) { return false; }
            distinctMemory.Charge(160L + memory.ValueBytes(row.Value));
            return true;
        }
        SetRow Shape(SetRow row) => plan.Shape == DocumentSetResultShape.Document ? new SetRow(row.Value, [row.Value]) : row;
        async IAsyncEnumerable<SetRow> InputAsync()
        {
            await foreach (var row in StreamQueryRowsAsync(database, operation, plan.Left, parameters, nested, needsInputValue, cancellationToken).ConfigureAwait(false))
            { yield return row; }
            if (plan.Query.Operation == OqlSetOperation.Union)
            {
                await foreach (var row in StreamQueryRowsAsync(database, operation, plan.Right, parameters, nested, needsInputValue, cancellationToken).ConfigureAwait(false))
                { yield return row; }
            }
        }
    }

    private static async IAsyncEnumerable<SetRow> StreamQueryRowsAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentStatementPlan plan, IReadOnlyDictionary<string, object?>? parameters, DocumentNestedExecutionContext nested,
        bool needsValue, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (plan is DocumentSetOperationPlan compound)
        {
            await foreach (var row in StreamSetRowsAsync(database, operation, compound, parameters, nested, true, needsValue, cancellationToken).ConfigureAwait(false))
            { yield return row; }
            yield break;
        }
        var logical = SourceLogical(plan);
        if (logical.Limit == 0) { yield break; }
        // Each top-level operand follows standalone SELECT: only its own subqueries,
        // expression sources or key bindings opt it into expression work accounting.
        // Correlated evaluations also need the current outer bindings, as nested SELECT does.
        var operandContext = nested.Bindings is not null || DocumentPlanner.HasSubquery(logical.Query, cancellationToken) ? nested : null;
        var evaluator = SourceEvaluator(database, operation, logical, parameters, cancellationToken, operandContext);
        if (!Streams(logical))
        {
            // Blocking stages belong to the operand's own semantics, not UNION ALL.
            await using var result = (DatabaseResultSet)await (operandContext is not null
                ? ExecuteNestedQueryAsync(database, operation, plan, parameters, cancellationToken, operandContext)
                : plan is DocumentPlan select ? ExecuteSelectAsync(database, operation, select, parameters, cancellationToken)
                : ValueTask.FromResult(ExecuteSystemCollection(database, operation, (DocumentSystemCollectionPlan)plan, parameters, cancellationToken))).ConfigureAwait(false);
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                using var active = evaluator.Memory.Reserve();
                active.Charge(384L + result.Columns.Count * 32L);
                var cells = new object?[result.Columns.Count];
                for (int index = 0; index < cells.Length; index++)
                { cells[index] = row.IsMissing(index) ? DocumentMissing.Instance : row.GetValue(index); active.Retain(cells[index]); }
                var value = needsValue ? SourceObject(logical, cells) : default;
                if (needsValue) { active.Retain(value); }
                yield return new SetRow(value, cells);
            }
            yield break;
        }
        await foreach (var row in SourceMatchesAsync(database, operation, plan, evaluator, parameters, operandContext, cancellationToken).ConfigureAwait(false))
        {
            using var active = evaluator.Memory.Reserve();
            var cells = await SourceProjectionAsync(logical, row, evaluator, operandContext, active).ConfigureAwait(false);
            active.Charge(384L + cells.Sum(cell => 64L + 3L * evaluator.Memory.ValueBytes(cell)));
            yield return new SetRow(needsValue ? SourceObject(logical, cells) : default, cells);
        }
    }

    private sealed record SetRow(JsonElement Value, object?[] Cells);
}
