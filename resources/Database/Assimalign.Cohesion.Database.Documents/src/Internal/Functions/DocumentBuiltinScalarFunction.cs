using System;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentBuiltinScalarFunction {Name}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentBuiltinScalarFunction.DebugView))]
internal sealed class DocumentBuiltinScalarFunction : DocumentScalarFunction
{
    internal DocumentBuiltinScalarFunction(OqlFunction definition)
        : base(definition.Name, definition.MinimumArguments, definition.MaximumArguments, definition.ResultType,
            definition.Name == "TYPE" ? DocumentNullBehavior.CalledOnNullOrMissingInput : DocumentNullBehavior.PropagateMissingOrNull,
            definition.ArgumentKinds.Select(ClassifyArgumentKind).ToArray()) { }
    protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments)
    {
        try { return new(new DocumentBuiltinCall(arguments.Context).Invoke(Name.ToString(), in arguments)); }
        catch (OverflowException error)
        { throw new DocumentPlanningException("CD02007", ErrorMessages.ExpressionEvaluatorEvaluateFailure2, innerException: error); }
    }
    private sealed class DebugView
    {
        private readonly DocumentBuiltinScalarFunction _target;
        public DebugView(DocumentBuiltinScalarFunction value) { _target = value; }
        public DocumentFunctionName Name => _target.Name;
    }

}
