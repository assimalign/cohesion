using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The scalar function <see cref="SqlScalarFunction.Create{T1, TResult}"/> builds: one delegate call
/// between two conversions that NativeAOT folds per instantiation (<see cref="SqlValueConverter{T}"/>).
/// </summary>
/// <typeparam name="T1">The argument's CLR type.</typeparam>
/// <typeparam name="TResult">The result's CLR type.</typeparam>
[global::System.Diagnostics.DebuggerDisplay("SqlTypedScalarFunction {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTypedScalarFunction<,>.DebugView))]
internal sealed class SqlTypedScalarFunction<T1, TResult> : SqlScalarFunction
{
    private readonly Func<T1, TResult> _body;

    /// <summary>Initializes the function; the type arguments map to SQL types here.</summary>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body.</param>
    /// <param name="volatility">How stable the result is.</param>
    /// <param name="nullBehavior">What a NULL argument does.</param>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    /// <exception cref="ArgumentException">The function is called on NULL input and an argument's type cannot hold NULL.</exception>
    internal SqlTypedScalarFunction(string name, Func<T1, TResult> body, SqlFunctionVolatility volatility, SqlNullBehavior nullBehavior)
        : base(name, [SqlValueConverter<T1>.Type], SqlValueConverter<TResult>.Type, volatility, nullBehavior)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (nullBehavior == SqlNullBehavior.CalledOnNullInput)
        {
            SqlValueConverter<T1>.RequireNullable(name, 1);
        }

        _body = body;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        => SqlValueConverter<TResult>.ToValue(_body(SqlValueConverter<T1>.FromValue(arguments[0])));

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlTypedScalarFunction<T1, TResult> _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTypedScalarFunction<T1, TResult> value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Sql.SqlFunctionKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.Database.Sql.SqlType? VariadicParameter => _target.VariadicParameter;
        public global::Assimalign.Cohesion.Database.Sql.SqlType ReturnType => _target.ReturnType;
        public global::Assimalign.Cohesion.Database.Sql.SqlFunctionVolatility Volatility => _target.Volatility;
        public global::Assimalign.Cohesion.Database.Sql.SqlNullBehavior NullBehavior => _target.NullBehavior;
        public bool IsNeverNull => _target.IsNeverNull;
    }
}
