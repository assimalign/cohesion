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
        DocumentUpdatePlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var evaluator = new DocumentExpressionEvaluator(plan.Statement.Alias, parameters, cancellationToken, transactionStart: operation.TransactionStart);
        var candidates = await ReadCandidatesAsync(database, operation, plan.Collection, plan.Access, cancellationToken).ConfigureAwait(false);
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
            if (!evaluator.Matches(plan.Statement.Predicate, context)) { continue; }
            var values = plan.Targets.Select(target => target.Value is null ? DocumentMissing.Instance : evaluator.Evaluate(target.Value, context)).ToArray();
            pending.Add((document.Id, DocumentJsonPatcher.Patch(json.RootElement, plan.Targets, values, plan.Statement.Location, cancellationToken)));
        }
        var collection = new DocumentCollection(database, plan.Collection, operation.Session);
        foreach (var document in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.EnsureActive();
            await collection.WriteAsync(operation.Context, document.Id, document.Content, null,
                insertOnly: false, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new DocumentCommandResult(pending.Count);
    }

    private static async ValueTask<DatabaseResult> ExecuteDeleteAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentDeletePlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var evaluator = new DocumentExpressionEvaluator(plan.Statement.Alias, parameters, cancellationToken, transactionStart: operation.TransactionStart);
        var candidates = await ReadCandidatesAsync(database, operation, plan.Collection, plan.Access, cancellationToken).ConfigureAwait(false);
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
            if (evaluator.Matches(plan.Statement.Predicate, new DocumentEvaluationContext(json.RootElement, entry.Id, entry.Version)))
            {
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
        return new DocumentCommandResult(affected);
    }

    private static async ValueTask<DatabaseResult> ExecuteWriteAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentCollectionMetadata metadata, OqlExpression statement, IReadOnlyList<(OqlExpression Key, OqlExpression Value)> values,
        (DocumentStatementPlan Source, int KeyOrdinal, int? ValueOrdinal)? select, bool insertOnly,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        string verb = statement is OqlUpsertExpression ? "UPSERT" : "INSERT";
        // Materialize and validate the complete statement snapshot before the first mutation.
        var pending = new List<(DocumentId Id, byte[] Content)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (select is { } sourcePlan)
        {
            await using var result = (DatabaseResultSet)(sourcePlan.Source switch
            {
                DocumentPlan source => await ExecuteSelectAsync(database, operation, source, parameters, cancellationToken, writeVerb: verb).ConfigureAwait(false),
                DocumentSystemCollectionPlan source => ExecuteSystemCollection(database, operation, source, parameters, cancellationToken, writeVerb: verb),
                _ => throw new DocumentPlanningException("COHDOC001", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValuesContext, verb), statement.Location),
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
            var evaluator = new DocumentExpressionEvaluator(null, parameters, cancellationToken, writeVerb: verb, transactionStart: operation.TransactionStart);
            foreach (var pair in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(evaluator.Evaluate(pair.Key, default), evaluator.Evaluate(pair.Value, default));
            }
        }
        var collection = new DocumentCollection(database, metadata, operation.Session);
        foreach (var document in pending)
        {
            operation.EnsureActive();
            await collection.WriteAsync(operation.Context, document.Id, document.Content, null,
                insertOnly, cancellationToken).ConfigureAwait(false);
        }
        return new DocumentCommandResult(pending.Count);

        void Add(object? key, object? value)
        {
            if (key is not string text || string.IsNullOrWhiteSpace(text))
            {
                throw new DocumentPlanningException("COHDOC008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteKeyInvalid, verb), statement.Location);
            }
            if (value is DocumentMissing)
            {
                throw new DocumentPlanningException("COHDOC008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValueMissing, verb), statement.Location);
            }
            var id = new DocumentId(text);
            if (!keys.Add(text)) { throw DocumentDuplicateKeyException.RepeatedInStatement(metadata.Name, id); }
            byte[] content;
            try
            {
                content = DocumentJsonValue.Encode(value, verb);
                DocumentStorage.ValidateContent(content);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new DocumentPlanningException("COHDOC008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteJsonInvalid, verb), statement.Location, exception);
            }
            pending.Add((id, content));
        }
    }
}
