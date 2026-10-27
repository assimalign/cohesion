using System;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentTypedScalarFunction {Name}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentTypedScalarFunction<,,,,>.DebugView))]
internal sealed class DocumentTypedScalarFunction<T1, T2, T3, T4, TResult> : DocumentScalarFunction
{
    private readonly Func<T1, T2, T3, T4, TResult> _body;
    internal DocumentTypedScalarFunction(DocumentFunctionName name, Func<T1, T2, T3, T4, TResult> body, DocumentNullBehavior nullBehavior)
        : base(name, 4, 4, DocumentTypedValue<TResult>.Type, nullBehavior, DocumentTypedValue<T1>.Kind, DocumentTypedValue<T2>.Kind, DocumentTypedValue<T3>.Kind, DocumentTypedValue<T4>.Kind)
    {
        ArgumentNullException.ThrowIfNull(body);
        DocumentTypedValue<T1>.RequireNullable(nullBehavior);
        DocumentTypedValue<T2>.RequireNullable(nullBehavior);
        DocumentTypedValue<T3>.RequireNullable(nullBehavior);
        DocumentTypedValue<T4>.RequireNullable(nullBehavior);
        _body = body;
    }
    protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments)
        => DocumentTypedValue<TResult>.Write(_body(DocumentTypedValue<T1>.Read(arguments[0]), DocumentTypedValue<T2>.Read(arguments[1]), DocumentTypedValue<T3>.Read(arguments[2]), DocumentTypedValue<T4>.Read(arguments[3])));

    private sealed class DebugView
    {
        private readonly DocumentTypedScalarFunction<T1, T2, T3, T4, TResult> _target;
        public DebugView(DocumentTypedScalarFunction<T1, T2, T3, T4, TResult> value) { _target = value; }
        public DocumentFunctionName Name => _target.Name;
    }
}
