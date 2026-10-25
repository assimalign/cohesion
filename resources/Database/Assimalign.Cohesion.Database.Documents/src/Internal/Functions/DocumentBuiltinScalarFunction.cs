using System;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentBuiltinScalarFunction {Name}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentBuiltinScalarFunction.DebugView))]
internal sealed class DocumentBuiltinScalarFunction : DocumentScalarFunction
{
    internal DocumentBuiltinScalarFunction(OqlFunction definition)
        : base(definition.Name, definition.MinimumArguments, definition.MaximumArguments, definition.ResultType,
            definition.Name == "TYPE" ? DocumentNullBehavior.CalledOnNullOrMissingInput : DocumentNullBehavior.PropagateMissingOrNull,
            ArgumentKindsFor(definition)) { }

    // Reuse the language's existing public predicate rather than exporting its private array
    // just to construct the engine's registration metadata. Variadic native calls repeat kinds.
    private static DocumentFunctionArgumentKind[] ArgumentKindsFor(OqlFunction definition)
    {
        var result = new DocumentFunctionArgumentKind[definition.MaximumArguments == int.MaxValue ? definition.MinimumArguments : definition.MaximumArguments];
        ReadOnlySpan<JsonValueKind> valuedKinds = [JsonValueKind.True, JsonValueKind.Number, JsonValueKind.String, JsonValueKind.Array, JsonValueKind.Object];
        for (int index = 0; index < result.Length; index++)
        {
            foreach (var kind in valuedKinds)
            { if (definition.AcceptsArgument(index, kind)) { result[index] |= ClassifyArgumentKind(kind); } }
        }
        return result;
    }
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
