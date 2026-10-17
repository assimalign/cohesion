using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    private static async ValueTask<DatabaseResult> ExecuteInsertAsync(DocumentDatabase database, DocumentOperation operation,
        DocumentCollectionMetadata metadata, OqlInsertExpression statement, DocumentInsertSelectPlan? select,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        // Materialize and validate the complete statement snapshot before the first mutation.
        var pending = new List<(DocumentId Id, byte[] Content)>();
        if (select is not null)
        {
            await using var result = (DatabaseResultSet)(select.Source switch
            {
                DocumentPlan source => await ExecuteSelectAsync(database, operation, source, parameters, cancellationToken, forInsert: true).ConfigureAwait(false),
                DocumentSystemCollectionPlan source => ExecuteSystemCollection(database, operation, source, parameters, cancellationToken, forInsert: true),
                _ => throw new DocumentPlanningException("COHDOC001", ErrorMessages.InsertValuesContext, statement.Location),
            });
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                object? Read(int ordinal) => row.IsMissing(ordinal) ? DocumentMissing.Instance : row.GetValue(ordinal);
                object? value = select.ValueOrdinal is int valueOrdinal ? Read(valueOrdinal) :
                    DocumentJsonValue.Object(result.Columns.Select(column => new KeyValuePair<string, object?>(column.Name, Read(column.Ordinal))), forInsert: true);
                Add(Read(select.KeyOrdinal), value);
            }
        }
        else
        {
            var evaluator = new DocumentExpressionEvaluator(null, parameters, cancellationToken, forInsert: true);
            foreach (var pair in statement.Values)
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
                insertOnly: true, cancellationToken).ConfigureAwait(false);
        }
        return new DocumentCommandResult(pending.Count);

        void Add(object? key, object? value)
        {
            if (key is not string text || string.IsNullOrWhiteSpace(text))
            {
                throw new DocumentPlanningException("COHDOC008", ErrorMessages.InsertKeyInvalid, statement.Location);
            }
            if (value is DocumentMissing)
            {
                throw new DocumentPlanningException("COHDOC008", ErrorMessages.InsertValueMissing, statement.Location);
            }
            var id = new DocumentId(text);
            byte[] content;
            try
            {
                content = DocumentJsonValue.Encode(value);
                DocumentStorage.ValidateContent(content);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new DocumentPlanningException("COHDOC008", ErrorMessages.InsertJsonInvalid, statement.Location, exception);
            }
            pending.Add((id, content));
        }
    }
}
