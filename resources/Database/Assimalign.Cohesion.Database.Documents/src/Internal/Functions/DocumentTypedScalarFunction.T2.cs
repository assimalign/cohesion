using System;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentTypedScalarFunction {Name}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentTypedScalarFunction<,>.DebugView))]
internal sealed class DocumentTypedScalarFunction<T1, TResult> : DocumentScalarFunction
{
    private readonly Func<T1, TResult> _body;
    internal DocumentTypedScalarFunction(DocumentFunctionName name, Func<T1, TResult> body, DocumentNullBehavior nullBehavior)
        : base(name, 1, 1, DocumentTypedValue<TResult>.Type, nullBehavior, DocumentTypedValue<T1>.Kind)
    {
        ArgumentNullException.ThrowIfNull(body);
        DocumentTypedValue<T1>.RequireNullable(nullBehavior);
        _body = body;
    }
    protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments)
        => DocumentTypedValue<TResult>.Write(_body(DocumentTypedValue<T1>.Read(arguments[0])));

    private sealed class DebugView
    {
        private readonly DocumentTypedScalarFunction<T1, TResult> _target;
        public DebugView(DocumentTypedScalarFunction<T1, TResult> value) { _target = value; }
        public DocumentFunctionName Name => _target.Name;
    }
}
