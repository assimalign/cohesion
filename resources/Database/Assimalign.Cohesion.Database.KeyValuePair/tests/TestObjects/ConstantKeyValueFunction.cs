using System;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

internal sealed class ConstantKeyValueFunction : KeyValueFunction
{
    private readonly Func<object?> _body;
    public ConstantKeyValueFunction(KeyValueFunctionName name, DatabaseType type, Func<object?> body) : base(name, [], type) { _body = body; }
    protected override object? InvokeCore(scoped in KeyValueArguments arguments) => _body();
}
