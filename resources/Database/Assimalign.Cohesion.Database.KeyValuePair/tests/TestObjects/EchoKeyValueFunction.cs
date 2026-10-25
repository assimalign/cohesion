using System;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

internal sealed class EchoKeyValueFunction : KeyValueFunction
{
    public EchoKeyValueFunction(KeyValueFunctionName name, DatabaseType type)
        : base(name, [type], type, KeyValueNullBehavior.CalledOnNullInput) { }

    public int Calls { get; private set; }
    public DatabaseName Database { get; private set; }

    protected override object? InvokeCore(scoped in KeyValueArguments arguments)
    {
        Calls++;
        Database = arguments.Context.Database;
        return arguments[0];
    }
}
