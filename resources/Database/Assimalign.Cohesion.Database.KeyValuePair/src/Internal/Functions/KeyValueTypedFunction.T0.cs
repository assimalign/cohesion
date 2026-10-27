using System;
using System.Diagnostics;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

[DebuggerDisplay("KeyValueFunction")]
[DebuggerTypeProxy(typeof(KeyValueTypedFunction<>.DebugView))]
internal sealed class KeyValueTypedFunction<TResult> : KeyValueFunction
{
    private readonly Func<TResult> _body;

    internal KeyValueTypedFunction(KeyValueFunctionName name, Func<TResult> body, KeyValueNullBehavior nullBehavior)
        : base(name, [], KeyValueFunctionValues.ClrType<TResult>(), nullBehavior)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
    }

    protected override object? InvokeCore(scoped in KeyValueArguments arguments) => _body();

    private sealed class DebugView
    {
        private readonly KeyValueTypedFunction<TResult> _target;

        public DebugView(KeyValueTypedFunction<TResult> value) { _target = value; }

        public KeyValueFunctionName Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Types.DatabaseType ReturnType => _target.ReturnType;
        public KeyValueNullBehavior NullBehavior => _target.NullBehavior;
    }
}
