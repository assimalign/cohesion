using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

internal sealed class FiveArgumentKeyValueFunction : KeyValueFunction
{
    public FiveArgumentKeyValueFunction() : base("f5", [DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Int64], DatabaseType.Int64) { }
    protected override object? InvokeCore(scoped in KeyValueArguments arguments)
        => arguments.Get<long>(0) + arguments.Get<long>(1) + arguments.Get<long>(2) + arguments.Get<long>(3) + arguments.Get<long>(4);
}
