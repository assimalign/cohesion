using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

internal sealed class DocumentWrongResultFunction : DocumentScalarFunction
{
    internal DocumentWrongResultFunction() : base("wrong_result", 0, 0, DatabaseType.String) { }
    protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments) => DocumentValue.FromInt64(1);
}
