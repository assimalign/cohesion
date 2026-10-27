using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    // Share the existing lazy constructor-cache slot with execution-only state so
    // scalar planning does not pay for aggregate budget and operation fields.
    [global::System.Diagnostics.DebuggerDisplay("EvaluationState")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(EvaluationState.DebugView))]
    private sealed class EvaluationState
    {
        private readonly DocumentOperation? _operation;
        private DocumentAggregateMemory? _memory;

        internal EvaluationState(DocumentOperation? operation)
        {
            _operation = operation;
        }

        internal Dictionary<(OqlExpression, int), (bool Constant, object? Value)>? Constructors { get; set; }

        internal DocumentAggregateMemory GetMemory(CancellationToken cancellationToken)
            => _memory ??= _operation?.GetStatementMemory(cancellationToken) ??
                new DocumentAggregateMemory(64L * 1024 * 1024, cancellationToken);

        private sealed class DebugView
        {
            public DebugView(EvaluationState value) { _ = value; }
        }
    }
}
