using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

internal sealed class DocumentArgumentMaskFunction : DocumentScalarFunction
{
    internal DocumentArgumentMaskFunction(DocumentFunctionArgumentKind kind)
        : base("mask_value", 1, 1, DatabaseType.String, DocumentNullBehavior.PropagateMissingOrNull, kind) { }
    protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments) => DocumentValue.FromString("accepted");
}
