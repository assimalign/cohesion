using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The scalar function <see cref="SqlScalarFunction.Create{T1, TResult}"/> builds: one delegate call
/// between two conversions that NativeAOT folds per instantiation (<see cref="SqlValueConverter{T}"/>).
/// </summary>
/// <typeparam name="T1">The argument's CLR type.</typeparam>
/// <typeparam name="TResult">The result's CLR type.</typeparam>
internal sealed class SqlTypedScalarFunction<T1, TResult> : SqlScalarFunction
{
    private readonly Func<T1, TResult> _body;

    /// <summary>Initializes the function; the type arguments map to SQL types here.</summary>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body.</param>
    /// <param name="volatility">How stable the result is.</param>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    internal SqlTypedScalarFunction(string name, Func<T1, TResult> body, SqlFunctionVolatility volatility)
        : base(name, [SqlValueConverter<T1>.Type], SqlValueConverter<TResult>.Type, volatility)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        => SqlValueConverter<TResult>.ToValue(_body(SqlValueConverter<T1>.FromValue(arguments[0])));
}

/// <summary>The scalar function <see cref="SqlScalarFunction.Create{T1, T2, TResult}"/> builds.</summary>
/// <typeparam name="T1">The first argument's CLR type.</typeparam>
/// <typeparam name="T2">The second argument's CLR type.</typeparam>
/// <typeparam name="TResult">The result's CLR type.</typeparam>
internal sealed class SqlTypedScalarFunction<T1, T2, TResult> : SqlScalarFunction
{
    private readonly Func<T1, T2, TResult> _body;

    /// <summary>Initializes the function; the type arguments map to SQL types here.</summary>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body.</param>
    /// <param name="volatility">How stable the result is.</param>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    internal SqlTypedScalarFunction(string name, Func<T1, T2, TResult> body, SqlFunctionVolatility volatility)
        : base(name, [SqlValueConverter<T1>.Type, SqlValueConverter<T2>.Type], SqlValueConverter<TResult>.Type, volatility)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        => SqlValueConverter<TResult>.ToValue(_body(
            SqlValueConverter<T1>.FromValue(arguments[0]),
            SqlValueConverter<T2>.FromValue(arguments[1])));
}

/// <summary>The scalar function <see cref="SqlScalarFunction.Create{T1, T2, T3, TResult}"/> builds.</summary>
/// <typeparam name="T1">The first argument's CLR type.</typeparam>
/// <typeparam name="T2">The second argument's CLR type.</typeparam>
/// <typeparam name="T3">The third argument's CLR type.</typeparam>
/// <typeparam name="TResult">The result's CLR type.</typeparam>
internal sealed class SqlTypedScalarFunction<T1, T2, T3, TResult> : SqlScalarFunction
{
    private readonly Func<T1, T2, T3, TResult> _body;

    /// <summary>Initializes the function; the type arguments map to SQL types here.</summary>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body.</param>
    /// <param name="volatility">How stable the result is.</param>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    internal SqlTypedScalarFunction(string name, Func<T1, T2, T3, TResult> body, SqlFunctionVolatility volatility)
        : base(name, [SqlValueConverter<T1>.Type, SqlValueConverter<T2>.Type, SqlValueConverter<T3>.Type],
            SqlValueConverter<TResult>.Type, volatility)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        => SqlValueConverter<TResult>.ToValue(_body(
            SqlValueConverter<T1>.FromValue(arguments[0]),
            SqlValueConverter<T2>.FromValue(arguments[1]),
            SqlValueConverter<T3>.FromValue(arguments[2])));
}

/// <summary>The scalar function <see cref="SqlScalarFunction.Create{T1, T2, T3, T4, TResult}"/> builds.</summary>
/// <typeparam name="T1">The first argument's CLR type.</typeparam>
/// <typeparam name="T2">The second argument's CLR type.</typeparam>
/// <typeparam name="T3">The third argument's CLR type.</typeparam>
/// <typeparam name="T4">The fourth argument's CLR type.</typeparam>
/// <typeparam name="TResult">The result's CLR type.</typeparam>
internal sealed class SqlTypedScalarFunction<T1, T2, T3, T4, TResult> : SqlScalarFunction
{
    private readonly Func<T1, T2, T3, T4, TResult> _body;

    /// <summary>Initializes the function; the type arguments map to SQL types here.</summary>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body.</param>
    /// <param name="volatility">How stable the result is.</param>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    internal SqlTypedScalarFunction(string name, Func<T1, T2, T3, T4, TResult> body, SqlFunctionVolatility volatility)
        : base(name, [SqlValueConverter<T1>.Type, SqlValueConverter<T2>.Type, SqlValueConverter<T3>.Type, SqlValueConverter<T4>.Type],
            SqlValueConverter<TResult>.Type, volatility)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        => SqlValueConverter<TResult>.ToValue(_body(
            SqlValueConverter<T1>.FromValue(arguments[0]),
            SqlValueConverter<T2>.FromValue(arguments[1]),
            SqlValueConverter<T3>.FromValue(arguments[2]),
            SqlValueConverter<T4>.FromValue(arguments[3])));
}
