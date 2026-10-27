using System.Globalization;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

internal sealed class DocumentContextFunction : DocumentScalarFunction
{
    internal DocumentContextFunction() : base("call_context", 0, 0, DatabaseType.String) { }
    protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments)
        => DocumentValue.FromString(arguments.Context.Database.ToString() + "|" +
            arguments.Context.TransactionTimestamp?.ToString("O", CultureInfo.InvariantCulture));
}
