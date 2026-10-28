using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    // Results stay private until every write and projection succeeds. Evaluate each image
    // inside the operation; its abort path owns rollback, including an explicit transaction.
    [global::System.Diagnostics.DebuggerDisplay("ReturningRows")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(ReturningRows.DebugView))]
    private sealed class ReturningRows : IDisposable
    {
        private readonly DocumentLogicalPlan _plan;
        private readonly DocumentExpressionEvaluator _evaluator;
        private readonly DocumentOperation _operation;
        private readonly CancellationToken _cancellationToken;
        private readonly long _limit;
        private readonly bool _wholeDocument;
        private readonly List<EvaluatedRow> _rows = [];
        private readonly DatabaseColumn[] _columns;
        private readonly DocumentRowWriter _writer;
        private readonly DocumentAggregateMemory _memory;

        internal ReturningRows(DocumentDatabase database, DocumentOperation operation, DocumentLogicalPlan plan,
            IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
        {
            _plan = plan;
            _operation = operation;
            _cancellationToken = cancellationToken;
            _limit = database.Engine.EngineOptions.StatementMemoryBudget;
            _memory = operation.GetStatementMemory(cancellationToken);
            _wholeDocument = plan.Projections.Count == 1 && plan.Projections[0].Expression is OqlStarExpression;
            _evaluator = new DocumentExpressionEvaluator(plan.Query.Alias, parameters, cancellationToken,
                transactionStart: operation.TransactionStart, functions: database.Engine.Functions, databaseName: database.Name,
                resultClause: "RETURNING");
            Charge(128L + plan.Projections.Count * 64L);
            long headerBytes = 5;
            foreach (var projection in plan.Projections)
            {
                Charge(projection.Name.Length * 2L);
                headerBytes += 5L + Encoding.UTF8.GetByteCount(projection.Name);
            }
            if (headerBytes > ProtocolFrameHeader.MaxPayloadLength)
            { throw new DocumentPlanningException("CD05016", ErrorMessages.ResultTooLarge, plan.Query.Location); }
            _columns = plan.Projections.Select((projection, index) => new DatabaseColumn
                { Name = projection.Name, Ordinal = index, Type = projection.Type }).ToArray();
            _writer = new DocumentRowWriter();
        }

        internal void Add(Document document)
        {
            _operation.EnsureActive();
            _cancellationToken.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions { MaxDepth = 128 });
            var context = new DocumentEvaluationContext(json.RootElement, document.Id.Value, document.Version.Value);
            var values = new object?[_plan.Projections.Count];
            // Covers the row, value array, list capacity and final result/normalization arrays.
            Charge(192L + values.Length * 32L);
            for (int index = 0; index < values.Length; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var value = _evaluator.EvaluateProjection(_plan.Projections[index].Expression, context, null, DocumentRowWriter.WritesBareValue(_plan.Query));
                if (_plan.Query.IsValue && value is DocumentMissing) { value = null; }
                Charge(value switch
                {
                    string text => 32L + text.Length * 2L,
                    JsonElement element => JsonBytes(element),
                    _ => 32L,
                });
                values[index] = value is JsonElement owned ? owned.Clone() : value;
            }
            // Star retains the original image in addition to the independently owned JSON
            // cell. Charge that byte array once, separately from the clone's text/metadata.
            if (_wholeDocument) { Charge(document.Content.Length); }
            var rawContent = _wholeDocument ? document.Content : default;
            // Depth was checked while counting JSON tokens. Preflight serialization while
            // rollback is still possible, preserving every other failure's own identity.
            var row = new DocumentQueryResult.DocumentQueryRow(values, rawContent, _plan.Query.IsValue || _wholeDocument);
            if (_writer.Write(row, _columns, DocumentRowWriter.WritesBareValue(_plan.Query), _plan.Query.Location, "RETURNING").Length > ProtocolFrameHeader.MaxPayloadLength)
            { throw new DocumentPlanningException("CD05016", ErrorMessages.ResultTooLarge, _plan.Query.Location); }
            // Evaluated JSON values own their clones. Do not retain a disposed JsonDocument
            // or an unprojected image through the row's evaluation context.
            _rows.Add(new EvaluatedRow(values, new DocumentEvaluationContext(default,
                RawContent: _wholeDocument ? document.Content : default), null, _rows.Count));
        }

        internal DocumentQueryResult Complete(long affectedCount)
            => CreateResult(_plan, _rows, _cancellationToken, affectedCount);

        public void Dispose() => _writer.Dispose();

        private long JsonBytes(JsonElement element)
            // JsonDocument clones keep UTF-8 text and 12 bytes of metadata per token.
            // Include 128 bytes for the document/arrays/boxed element; enumerate structs
            // without allocating a UTF-16 copy or a depth-dependent reader stack.
            => 128L + JsonMarshal.GetRawUtf8Value(element).Length +
                12L * JsonTokens(element, _plan.Query.IsValue || _wholeDocument ? 0 : 1);

        private long JsonTokens(JsonElement element, int enclosingDepth)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) { return 1; }
            if (enclosingDepth == 256)
            { throw new DocumentPlanningException("CD05019", ErrorMessages.ReturningDepthExceeded, _plan.Query.Location); }
            long tokens = 2; // containers retain a start and an end token
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject()) { tokens += 1 + JsonTokens(property.Value, enclosingDepth + 1); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { tokens += JsonTokens(item, enclosingDepth + 1); }
            }
            return tokens;
        }

        private void Charge(long bytes)
        {
            if (!_memory.TryCharge(bytes))
            {
                throw new DocumentPlanningException("CD05018", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.ReturningMemoryExceeded, _limit), _plan.Query.Location);
            }
        }

        private sealed class DebugView
        {
            public DebugView(ReturningRows value) { _ = value; }
        }
    }
}
