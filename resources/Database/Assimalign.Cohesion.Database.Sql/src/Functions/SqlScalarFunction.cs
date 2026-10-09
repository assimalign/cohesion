using System;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A function that computes one value from one row's arguments: the engine's <c>UPPER</c>,
/// <c>LENGTH</c> and <c>ABS</c>, and an application's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>An inverted seam:</b> the engine drives it and the standard library and applications
/// implement it, by deriving and overriding <see cref="InvokeCore"/>, or through the typed
/// shorthands (<see cref="Create{T1, TResult}(string, Func{T1, TResult}, SqlFunctionVolatility)"/>
/// and its arities), which create an internal sealed leaf.
/// </para>
/// <para>
/// <b>The call (NVI).</b> <see cref="Invoke"/> checks the argument count, returns NULL without a call
/// when the function is strict and an argument is NULL (PostgreSQL's <c>EEOP_FUNCEXPR_STRICT</c>), and
/// calls <see cref="InvokeCore"/> once. Anything the core throws other than
/// <see cref="OperationCanceledException"/>, <see cref="InsufficientExecutionStackException"/>,
/// <see cref="OutOfMemoryException"/> or a <see cref="DatabaseException"/> fails the statement as
/// <c>COHSQLE007</c>, naming the function, with the original as the inner exception; a
/// <see cref="DatabaseException"/> the core throws reaches the statement unchanged.
/// </para>
/// <para>
/// <b>Thread safety.</b> One instance serves every session at once: the core must be stateless or
/// thread-safe.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class SqlScalarFunction : SqlFunction
{
    /// <summary>Initializes a scalar function.</summary>
    /// <param name="name">The name calls use, matched ignoring case.</param>
    /// <param name="parameters">The parameter types, in order.</param>
    /// <param name="returnType">The result type.</param>
    /// <param name="volatility">
    /// How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default. Only an
    /// <see cref="SqlFunctionVolatility.Immutable"/> function is folded or admitted in a CHECK.
    /// </param>
    /// <param name="nullBehavior">What a NULL argument does; strict by default.</param>
    /// <param name="variadicParameter">The type of any number of trailing arguments, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="returnType"/> or a parameter type is null.</exception>
    /// <exception cref="ArgumentException">The name is blank, or the result type is a pseudo-type the parameters cannot determine.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enumeration argument is not defined.</exception>
    protected SqlScalarFunction(string name, ReadOnlySpan<SqlType> parameters, SqlType returnType,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput,
        SqlType? variadicParameter = null)
        : base(name, SqlFunctionKind.Scalar, parameters, returnType, volatility, nullBehavior, variadicParameter)
    {
    }

    /// <summary>Calls the function.</summary>
    /// <param name="arguments">The arguments, already of the declared types.</param>
    /// <returns>The result; NULL without a call when the function is strict and an argument is NULL.</returns>
    /// <exception cref="ArgumentException">The number of arguments does not match the parameters.</exception>
    /// <exception cref="DatabaseException">
    /// The function failed: <c>COHSQLE007</c> wrapping what it threw, or the database exception it threw.
    /// </exception>
    public SqlValue Invoke(scoped in SqlArguments arguments)
    {
        ThrowIfArgumentCountInvalid(arguments.Count);
        if (NullBehavior == SqlNullBehavior.ReturnsNullOnNullInput && arguments.HasNull)
        {
            return SqlValue.Null;
        }

        try
        {
            return InvokeCore(in arguments);
        }
        catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
        {
            throw SqlEvaluationException.FunctionFailed(Name, exception);
        }
    }

    /// <summary>Computes the function's value; the engine calls it through <see cref="Invoke"/>.</summary>
    /// <param name="arguments">
    /// The arguments: as many as the function declares, each of its parameter's type, and none NULL
    /// when the function is strict.
    /// </param>
    /// <returns>The result, of the declared type, or <see cref="SqlValue.Null"/>.</returns>
    protected abstract SqlValue InvokeCore(scoped in SqlArguments arguments);

    /// <summary>
    /// Creates a scalar function of one argument from a delegate. The CLR types map to SQL types
    /// when this method runs (<see cref="long"/> to <see cref="SqlType.BigInt"/>, <see cref="string"/>
    /// to <see cref="SqlType.Text"/>, and so on for every storage type, a nullable value type
    /// mapping as its underlying type), so an unsupported type fails here, not at the first call.
    /// The function is strict: the delegate never receives a NULL argument, and a
    /// <see langword="null"/> result is SQL NULL.
    /// </summary>
    /// <typeparam name="T1">The argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <returns>The function, to register with <see cref="SqlFunctionCollection.Add"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, TResult>(string name, Func<T1, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile)
        => new SqlTypedScalarFunction<T1, TResult>(name, body, volatility);

    /// <summary>Creates a scalar function of two arguments from a delegate; see <see cref="Create{T1, TResult}"/>.</summary>
    /// <typeparam name="T1">The first argument's CLR type.</typeparam>
    /// <typeparam name="T2">The second argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, T2, TResult>(string name, Func<T1, T2, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile)
        => new SqlTypedScalarFunction<T1, T2, TResult>(name, body, volatility);

    /// <summary>Creates a scalar function of three arguments from a delegate; see <see cref="Create{T1, TResult}"/>.</summary>
    /// <typeparam name="T1">The first argument's CLR type.</typeparam>
    /// <typeparam name="T2">The second argument's CLR type.</typeparam>
    /// <typeparam name="T3">The third argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile)
        => new SqlTypedScalarFunction<T1, T2, T3, TResult>(name, body, volatility);

    /// <summary>Creates a scalar function of four arguments from a delegate; see <see cref="Create{T1, TResult}"/>.</summary>
    /// <typeparam name="T1">The first argument's CLR type.</typeparam>
    /// <typeparam name="T2">The second argument's CLR type.</typeparam>
    /// <typeparam name="T3">The third argument's CLR type.</typeparam>
    /// <typeparam name="T4">The fourth argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, T2, T3, T4, TResult>(string name, Func<T1, T2, T3, T4, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile)
        => new SqlTypedScalarFunction<T1, T2, T3, T4, TResult>(name, body, volatility);
}
