using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentExchange")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Client.Internal.DocumentExchange.DebugView))]
internal sealed class DocumentExchange : DatabaseProtocolExchange<DocumentExchangeResult>
{
    private readonly ProtocolFrame _request;
    private readonly DocumentProtocolCommandMessage? _command;

    internal DocumentExchange(string statement, IReadOnlyDictionary<string, object?>? parameters) : base(DocumentProtocol.Family, transactionStatus: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        _request = new((ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage(statement, DocumentJsonValues.Parameters(parameters)).Encode());
    }

    internal DocumentExchange(DocumentProtocolCommandMessage command) : base(DocumentProtocol.Family, transactionStatus: true)
    {
        _command = command;
        _request = new((ProtocolMessageType)DocumentProtocolMessageType.Command, command.Encode());
    }

    protected override async ValueTask<DocumentExchangeResult> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(_request, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        DocumentProtocolResultHeaderMessage? header = null;
        var columns = new List<DocumentColumn>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<object?[]>();
        var documents = new List<JsonElement>();
        var rawDocuments = new List<ReadOnlyMemory<byte>>();
        var collections = new List<string>();
        Document? stored = null;
        long count = 0;
        while (true)
        {
            var frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ProtocolException(ErrorMessages.ExchangeIncomplete);
            switch ((byte)frame.Type)
            {
                case (byte)DocumentProtocolMessageType.ResultHeader:
                    if (_command is not null || header is not null || count != 0) { throw new ProtocolException(ErrorMessages.ResultHeaderUnexpected); }
                    header = DocumentProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    for (int index = 0; index < header.Columns.Count; index++)
                    {
                        var column = header.Columns[index];
                        columns.Add(new(column.Name, index, (DatabaseType)column.Type));
                        if (!ordinals.TryAdd(column.Name, index)) { throw new ProtocolException(ErrorMessages.RowWidthInvalid); }
                    }
                    break;
                case (byte)DocumentProtocolMessageType.Document:
                    if (header is null)
                    {
                        throw new ProtocolException(ErrorMessages.ResultKindUnexpected);
                    }
                    var result = DocumentProtocolResultMessage.Decode(frame.Payload.Span);
                    using (var json = JsonDocument.Parse(result.Json, new JsonDocumentOptions { MaxDepth = 256 }))
                    {
                        if (header.WholeDocuments)
                        {
                            var document = json.RootElement.Clone();
                            documents.Add(document);
                            rawDocuments.Add(result.Json);
                            rows.Add([document]);
                        }
                        else
                        {
                            if (json.RootElement.ValueKind != JsonValueKind.Object)
                            {
                                throw new ProtocolException(ErrorMessages.RowWidthInvalid);
                            }
                            var values = new object?[columns.Count];
                            Array.Fill(values, default(JsonElement));
                            foreach (var property in json.RootElement.EnumerateObject())
                            {
                                if (!ordinals.TryGetValue(property.Name, out int ordinal) ||
                                    values[ordinal] is not JsonElement { ValueKind: JsonValueKind.Undefined })
                                {
                                    throw new ProtocolException(ErrorMessages.RowWidthInvalid);
                                }
                                values[ordinal] = DocumentJsonValues.Read(property.Value, columns[ordinal].Type);
                            }
                            rows.Add(values);
                        }
                    }
                    count++;
                    break;
                case (byte)DocumentProtocolMessageType.StoredDocument:
                    if (_command?.Operation is not (DocumentProtocolOperation.Get or DocumentProtocolOperation.Put) || count != 0)
                    {
                        throw new ProtocolException(ErrorMessages.StoredDocumentUnexpected);
                    }
                    stored = DocumentProtocolStoredDocumentMessage.Decode(frame.Payload.Span).Document;
                    if (stored.Value.Id.Value != _command.Id) { throw new ProtocolException(ErrorMessages.DocumentIdentityMismatch); }
                    count++;
                    break;
                case (byte)DocumentProtocolMessageType.Collection:
                    if (_command?.Operation is not (DocumentProtocolOperation.CreateCollection or DocumentProtocolOperation.GetCollection or DocumentProtocolOperation.ListCollections))
                    {
                        throw new ProtocolException(ErrorMessages.CollectionUnexpected);
                    }
                    string name = DocumentProtocolCollectionMessage.Decode(frame.Payload.Span).Name;
                    if (_command.Operation == DocumentProtocolOperation.ListCollections ?
                        collections.Count > 0 && StringComparer.Ordinal.Compare(collections[^1], name) >= 0 : count != 0 || name != _command.Collection)
                    {
                        throw new ProtocolException(ErrorMessages.CollectionOrderInvalid);
                    }
                    collections.Add(name);
                    count++;
                    break;
                case (byte)DocumentProtocolMessageType.Complete:
                    var complete = DocumentProtocolCompleteMessage.Decode(frame.Payload.Span);
                    ValidateCompletion(complete, count, header);
                    return new(new DocumentResultSet(columns.AsReadOnly(), rows, complete.AffectedCount,
                        header?.WholeDocuments ?? false, documents.AsReadOnly(), header?.WholeDocuments == true ? rawDocuments : null), stored, collections.AsReadOnly());
                case (byte)DocumentProtocolMessageType.Failure:
                    var failure = DocumentProtocolFailureMessage.Decode(frame.Payload.Span);
                    rows.Clear();
                    documents.Clear();
                    rawDocuments.Clear();
                    collections.Clear();
                    stored = null;
                    MarkResponseComplete();
                    throw new DocumentClientException(failure.Code, failure.Message, diagnostics: failure.Diagnostics);
                case (byte)ProtocolMessageType.Error:
                    // Shared errors terminate sessions. Only the model's Failure certifies reuse.
                    var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
                    throw new DatabaseClientException(error.Code, error.Message);
                default:
                    throw new ProtocolException(ErrorMessages.ResponseFrameUnexpected);
            }
        }
    }

    private void ValidateCompletion(DocumentProtocolCompleteMessage complete, long count, DocumentProtocolResultHeaderMessage? header)
    {
        if (complete.ResultCount != count) { throw new ProtocolException(ErrorMessages.CompletionCountMismatch); }
        bool valid = _command?.Operation switch
        {
            null => header is null ? count == 0 && complete.AffectedCount >= 0 : complete.AffectedCount == -1,
            DocumentProtocolOperation.Get => count <= 1 && complete.AffectedCount == -1,
            DocumentProtocolOperation.Put => count == 1 && complete.AffectedCount == 1,
            DocumentProtocolOperation.Delete => count == 0 && complete.AffectedCount is 0 or 1,
            DocumentProtocolOperation.CreateCollection => count == 1 && complete.AffectedCount == 0,
            DocumentProtocolOperation.GetCollection => count == 1 && complete.AffectedCount == -1,
            DocumentProtocolOperation.DropCollection => count == 0 && complete.AffectedCount == 0,
            DocumentProtocolOperation.ListCollections => complete.AffectedCount == -1,
            _ => false,
        };
        if (!valid) { throw new ProtocolException(ErrorMessages.CompletionUnexpected); }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Documents.Client.Internal.DocumentExchange value)
        {
            _ = value;
        }
    }
}
