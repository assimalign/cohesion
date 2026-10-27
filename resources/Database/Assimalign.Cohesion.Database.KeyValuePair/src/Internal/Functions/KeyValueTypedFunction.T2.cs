using System;
using System.Diagnostics;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

[DebuggerDisplay("KeyValueFunction")]
[DebuggerTypeProxy(typeof(KeyValueTypedFunction<,,>.DebugView))]
internal sealed class KeyValueTypedFunction<T1, T2, TResult> : KeyValueFunction
{
    private readonly Func<T1, T2, TResult> _body;

    internal KeyValueTypedFunction(KeyValueFunctionName name, Func<T1, T2, TResult> body, KeyValueNullBehavior nullBehavior)
        : base(name, [KeyValueFunctionValues.ClrType<T1>(), KeyValueFunctionValues.ClrType<T2>()], KeyValueFunctionValues.ClrType<TResult>(), nullBehavior)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (nullBehavior == KeyValueNullBehavior.CalledOnNullInput && (!KeyValueFunctionValues.CanBeNull<T1>() || !KeyValueFunctionValues.CanBeNull<T2>()))
        {
            throw new ArgumentException(ErrorMessages.FunctionNullParameterInvalid, nameof(nullBehavior));
        }
        _body = body;
    }

    protected override object? InvokeCore(scoped in KeyValueArguments arguments) => _body(arguments.Get<T1>(0), arguments.Get<T2>(1));

    private sealed class DebugView
    {
        private readonly KeyValueTypedFunction<T1, T2, TResult> _target;

        public DebugView(KeyValueTypedFunction<T1, T2, TResult> value) { _target = value; }

        public KeyValueFunctionName Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Types.DatabaseType ReturnType => _target.ReturnType;
        public KeyValueNullBehavior NullBehavior => _target.NullBehavior;
    }
}
