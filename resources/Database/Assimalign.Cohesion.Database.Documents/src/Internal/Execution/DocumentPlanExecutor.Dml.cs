using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Execution;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    private static async ValueTask<DatabaseResult> ExecuteUpdateAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentUpdatePlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, DocumentNestedExecutionContext? nested = null)
    {
        var evaluator = new DocumentExpressionEvaluator(plan.Statement.Alias, parameters, cancellationToken, transactionStart: operation.TransactionStart, functions: database.Engine.Functions, databaseName: database.Name, operation: nested is null ? null : operation, nested: nested, sourceName: plan.Statement.Alias ?? plan.Statement.Collection);
        var candidates = await ReadCandidatesAsync(database, operation, plan.Collection, plan.Access, cancellationToken).ConfigureAwait(false);
        using var returning = plan.Returning is null ? null : new ReturningRows(database, operation, plan.Returning, parameters, cancellationToken, nested);
        using var pendingMemory = operation.GetStatementMemory(cancellationToken).Reserve();
        var pending = new List<(DocumentId Id, byte[] Content)>();
        // Freeze membership and every RHS against the statement snapshot before writing.
        // An indexed field may move across the seek range without revisiting its document.
        foreach (var entry in candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            operation.Session.LastQueryDocumentsRead++;
            var document = database.ReadDocument(entry);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            var context = new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version);
            if (!(nested is null ? evaluator.Matches(plan.Statement.Predicate, context) : await evaluator.MatchesAsync(plan.Statement.Predicate, context, null, nested).ConfigureAwait(false))) { continue; }
            long before = nested is null ? 0 : evaluator.Memory.Bytes - nested.RetainedBytes;
            var values = new object?[plan.Targets.Count];
            byte[] content;
            try
            {
                for (int index = 0; index < values.Length; index++)
                {
                    var value = plan.Targets[index].Value;
                    values[index] = value is null ? DocumentMissing.Instance : nested is null ? evaluator.Evaluate(value, context) :
                        await evaluator.EvaluateAsync(value, context, null, nested).ConfigureAwait(false);
                }
                content = DocumentJsonPatcher.Patch(json.RootElement, plan.Targets, values, plan.Statement.Location, cancellationToken);
            }
            finally { if (nested is not null) { evaluator.Memory.Release(evaluator.Memory.Bytes - nested.RetainedBytes - before); } }
            pendingMemory.Charge(160L + entry.Id.Length * 2L + content.Length);
            pending.Add((document.Id, content));
        }
        var versions = await StageNestedReturningAsync(database, operation, returning, plan.Returning, pending, pendingMemory, nested, cancellationToken).ConfigureAwait(false);
        var collection = new DocumentCollection(database, plan.Collection, operation.Session);
        for (int index = 0; index < pending.Count; index++)
        {
            var document = pending[index];
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            var stored = await collection.WriteAsync(operation.Context, document.Id, document.Content, null,
                insertOnly: false, cancellationToken, versions?[index]).ConfigureAwait(false);
            if (returning is not null && versions is null) { await returning.AddAsync(stored, nested).ConfigureAwait(false); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return returning?.Complete(pending.Count) ?? (DatabaseResult)new DocumentCommandResult(pending.Count);
    }

    private static async ValueTask<DatabaseResult> ExecuteDeleteAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentDeletePlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, DocumentNestedExecutionContext? nested = null)
    {
        var evaluator = new DocumentExpressionEvaluator(plan.Statement.Alias, parameters, cancellationToken, transactionStart: operation.TransactionStart, functions: database.Engine.Functions, databaseName: database.Name, operation: nested is null ? null : operation, nested: nested, sourceName: plan.Statement.Alias ?? plan.Statement.Collection);
        var candidates = await ReadCandidatesAsync(database, operation, plan.Collection, plan.Access, cancellationToken).ConfigureAwait(false);
        using var returning = plan.Returning is null ? null : new ReturningRows(database, operation, plan.Returning, parameters, cancellationToken, nested);
        using var pendingMemory = operation.GetStatementMemory(cancellationToken).Reserve();
        var pending = new List<DocumentId>();
        // Finish reading and evaluating the statement snapshot before changing any content
        // or index entry. A seek supplies candidates; the complete predicate decides membership.
        foreach (var entry in candidates.OrderBy(entry => entry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            operation.Session.LastQueryDocumentsRead++;
            var document = database.ReadDocument(entry);
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            if (nested is null ? evaluator.Matches(plan.Statement.Predicate, new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version)) :
                await evaluator.MatchesAsync(plan.Statement.Predicate, new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version), null, nested).ConfigureAwait(false))
            {
                if (returning is not null) { await returning.AddAsync(document, nested).ConfigureAwait(false); }
                pendingMemory.Charge(96L + entry.Id.Length * 2L);
                pending.Add(document.Id);
            }
        }
        var collection = new DocumentCollection(database, plan.Collection, operation.Session);
        long affected = 0;
        foreach (var id in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            if (await collection.DeleteAsync(operation.Context, id, null, cancellationToken).ConfigureAwait(false)) { affected++; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (affected != pending.Count) { throw new InvalidOperationException(ErrorMessages.DeleteCountMismatch); }
        return returning?.Complete(pending.Count) ?? (DatabaseResult)new DocumentCommandResult(pending.Count);
    }

    private static async ValueTask<DatabaseResult> ExecuteWriteAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentCollectionMetadata metadata, OqlExpression statement, IReadOnlyList<(OqlExpression Key, OqlExpression Value)> values,
        (DocumentStatementPlan Source, int KeyOrdinal, int? ValueOrdinal)? select, bool insertOnly, DocumentLogicalPlan? returningPlan,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken, DocumentNestedExecutionContext? nested = null)
    {
        string verb = statement is OqlUpsertExpression ? "UPSERT" : "INSERT";
        // Materialize and validate the complete statement snapshot before the first mutation.
        using var returning = returningPlan is null ? null : new ReturningRows(database, operation, returningPlan, parameters, cancellationToken, nested);
        using var pendingMemory = operation.GetStatementMemory(cancellationToken).Reserve();
        var pending = new List<(DocumentId Id, byte[] Content)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (select is { } sourcePlan)
        {
            await using var result = (DatabaseResultSet)(nested is not null ? await ExecuteNestedQueryAsync(database, operation, sourcePlan.Source, parameters, cancellationToken, nested).ConfigureAwait(false) : sourcePlan.Source switch
            {
                DocumentPlan source => await ExecuteSelectAsync(database, operation, source, parameters, cancellationToken, writeVerb: verb).ConfigureAwait(false),
                DocumentSystemCollectionPlan source => ExecuteSystemCollection(database, operation, source, parameters, cancellationToken, writeVerb: verb),
                _ => throw new DocumentPlanningException("CD05001", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValuesContext, verb), statement.Location),
            });
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                object? Read(int ordinal) => row.IsMissing(ordinal) ? DocumentMissing.Instance : row.GetValue(ordinal);
                object? value = sourcePlan.ValueOrdinal is int valueOrdinal ? Read(valueOrdinal) :
                    DocumentJsonValue.Object(result.Columns.Select(column => new KeyValuePair<string, object?>(column.Name, Read(column.Ordinal))), writeVerb: verb);
                Add(Read(sourcePlan.KeyOrdinal), value);
            }
        }
        else
        {
            var evaluator = new DocumentExpressionEvaluator(null, parameters, cancellationToken, writeVerb: verb, transactionStart: operation.TransactionStart, functions: database.Engine.Functions, databaseName: database.Name, operation: nested is null ? null : operation, nested: nested, sourceName: null);
            foreach (var pair in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long before = nested is null ? 0 : evaluator.Memory.Bytes - nested.RetainedBytes;
                long pendingBefore = pendingMemory.Bytes;
                try
                {
                    Add(nested is null ? evaluator.Evaluate(pair.Key, default) : await evaluator.EvaluateAsync(pair.Key, default, null, nested).ConfigureAwait(false),
                        nested is null ? evaluator.Evaluate(pair.Value, default) : await evaluator.EvaluateAsync(pair.Value, default, null, nested).ConfigureAwait(false));
                }
                finally
                {
                    if (nested is not null)
                    { evaluator.Memory.Release(evaluator.Memory.Bytes - nested.RetainedBytes - before - (pendingMemory.Bytes - pendingBefore)); }
                }
            }
        }
        var versions = await StageNestedReturningAsync(database, operation, returning, returningPlan, pending, pendingMemory, nested, cancellationToken).ConfigureAwait(false);
        var collection = new DocumentCollection(database, metadata, operation.Session);
        for (int index = 0; index < pending.Count; index++)
        {
            var document = pending[index];
            operation.EnsureActive();
            var stored = await collection.WriteAsync(operation.Context, document.Id, document.Content, null,
                insertOnly, cancellationToken, versions?[index]).ConfigureAwait(false);
            if (returning is not null && versions is null) { await returning.AddAsync(stored, nested).ConfigureAwait(false); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return returning?.Complete(pending.Count) ?? (DatabaseResult)new DocumentCommandResult(pending.Count);

        void Add(object? key, object? value)
        {
            if (key is not string text || string.IsNullOrWhiteSpace(text))
            {
                throw new DocumentPlanningException("CD02008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteKeyInvalid, verb), statement.Location);
            }
            if (value is DocumentMissing)
            {
                throw new DocumentPlanningException("CD02008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValueMissing, verb), statement.Location);
            }
            var id = new DocumentId(text);
            if (keys.Contains(text)) { throw DocumentDuplicateKeyException.RepeatedInStatement(metadata.Name, id); }
            // The key set and pending list share the string but own separate slots.
            // Reserve before either container retains it; no writes start until staging ends.
            pendingMemory.Charge(192L + text.Length * 2L);
            keys.Add(text);
            byte[] content;
            try
            {
                content = DocumentJsonValue.Encode(value, verb);
                DocumentStorage.ValidateContent(content);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new DocumentPlanningException("CD02008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteJsonInvalid, verb), statement.Location, exception);
            }
            pendingMemory.Charge(32L + content.Length);
            pending.Add((id, content));
        }
    }

    private static async ValueTask<ulong[]?> StageNestedReturningAsync(DocumentDatabase database, DocumentOperation operation,
        ReturningRows? returning, DocumentLogicalPlan? plan, IReadOnlyList<(DocumentId Id, byte[] Content)> pending,
        DocumentAggregateMemory.Reservation retained, DocumentNestedExecutionContext? nested, CancellationToken cancellationToken)
    {
        if (nested is null || returning is null || plan is null || !DocumentPlanner.HasSubquery(plan.Query)) { return null; }
        retained.Charge(32L + pending.Count * 8L);
        var versions = new ulong[pending.Count];
        // Reserve the actual versions first; both staged RETURNING and the eventual
        // writes use these identities. No branch or subquery is evaluated speculatively.
        for (int index = 0; index < pending.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            ulong version = (ulong)database.DataStorage.ReserveTransactionSequence();
            versions[index] = version;
            var document = pending[index];
            await returning.AddAsync(new Document(document.Id, new DocumentVersion(version), document.Content), nested).ConfigureAwait(false);
        }
        return versions;
    }
}
