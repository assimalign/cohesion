using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    internal DocumentVariableBinding? CaptureBindings(DocumentEvaluationContext document)
    {
        var state = _state as NestedEvaluationState;
        var bindings = state?.Outer;
        if ((state?.SourceName ?? _alias) is string source)
        { bindings = new DocumentVariableBinding(source, ReadJson(document.Content), bindings); }
        var frames = new Stack<DocumentVariableBinding>();
        for (var frame = document.Bindings; frame is not null; frame = frame.Parent) { frames.Push(frame); }
        foreach (var frame in frames) { bindings = new DocumentVariableBinding(frame.Name, frame.Value, bindings); }
        return bindings;
    }

    private sealed class NestedEvaluationState : EvaluationState
    {
        internal NestedEvaluationState(DocumentOperation? operation, DocumentVariableBinding? outer, string? sourceName) : base(operation)
        { Outer = outer; SourceName = sourceName; }

        internal DocumentVariableBinding? Outer { get; }
        internal string? SourceName { get; }
    }

    // Share the existing lazy constructor-cache slot with execution-only state so
    // scalar planning does not pay for aggregate budget and operation fields.
    [global::System.Diagnostics.DebuggerDisplay("EvaluationState")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(EvaluationState.DebugView))]
    private class EvaluationState
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
