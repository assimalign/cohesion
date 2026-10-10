using System;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A function that computes one value from one row's arguments: the engine's <c>UPPER</c>,
/// <c>LENGTH</c> and <c>ABS</c>, and an application's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>An inverted seam:</b> the engine drives it and the standard library and applications
/// implement it, by deriving and overriding <see cref="InvokeCore"/>, or through the typed
/// shorthands (<see cref="Create{T1, TResult}"/>
/// and its arities), which create an internal sealed leaf.
/// </para>
/// <para>
/// <b>The call (NVI).</b> <see cref="Invoke"/> checks the argument count, returns NULL without a call
/// when the function is strict and an argument is NULL (PostgreSQL's <c>EEOP_FUNCEXPR_STRICT</c>), and
/// calls <see cref="InvokeCore"/> once. Anything the core throws other than
/// <see cref="OperationCanceledException"/>, <see cref="InsufficientExecutionStackException"/>,
/// <see cref="OutOfMemoryException"/> or a <see cref="DatabaseException"/> fails the statement as
/// <c>COHSQLE007</c>, naming the function, with the original as the inner exception; a
/// <see cref="DatabaseException"/> the core throws reaches the statement and the client unchanged,
/// which is how a function raises an error of its own: a message that leads with its own code
/// (<c>APP001: ...</c>) keeps it. The result is checked against <see cref="SqlFunction.ReturnType"/>
/// (<see cref="InvokeCore"/>).
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

        SqlValue result;
        try
        {
            result = InvokeCore(in arguments);
        }
        catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
        {
            throw SqlEvaluationException.FunctionFailed(Name, exception);
        }

        return HasDeclaredResultType(result.Type, in arguments) ? result : CheckResultSlow(result, in arguments);
    }

    /// <summary>
    /// The engine's call of a resolved function over one row's arguments: the planner matched the
    /// argument count and the evaluator applied the strict short-circuit before converting a value,
    /// so this makes the one <see cref="InvokeCore"/> call, codes what it throws and checks the
    /// result, as <see cref="Invoke"/> does, and returns the result in the row's representation.
    /// </summary>
    /// <remarks>
    /// Every call the evaluator makes, of a built-in or an application's function, comes through
    /// here. The result is checked and converted where the core wrote it, so no copy of the
    /// 32-byte value is made between the core and the row; a value with a reference (text, or an
    /// argument handed back) becomes the row's object inline (<see cref="SqlValue.ToObject"/>).
    /// </remarks>
    /// <param name="arguments">The arguments: as many as the function declares, of their parameters' types.</param>
    /// <returns>The result as the row holds it.</returns>
    /// <exception cref="DatabaseException">The function failed (<c>COHSQLE007</c>), or the database exception it threw.</exception>
    internal object? InvokeResolved(scoped in SqlArguments arguments)
    {
        SqlValue result;
        try
        {
            result = InvokeCore(in arguments);
        }
        catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
        {
            throw SqlEvaluationException.FunctionFailed(Name, exception);
        }

        return HasDeclaredResultType(result.Type, in arguments) ? result.ToObject() : CheckResultToObject(result, in arguments);
    }

    /// <summary>
    /// The engine's call of a resolved function of one argument over a row value the strict
    /// short-circuit has admitted: the value converted to a <see cref="SqlValue"/> of its
    /// parameter's type, the one <see cref="InvokeCore"/> call, its failure coded and its result
    /// checked, as <see cref="Invoke"/> does, and the result returned in the row's representation.
    /// </summary>
    /// <remarks>
    /// The shape of every standard-library scalar and the most common application one, so the
    /// conversion, the call and the check share one frame: it holds the one value, the call's
    /// arguments and the result, and no buffer of four values for the prologue to clear. A value
    /// that has its parameter's type already, as a column of that type always does, is not passed
    /// through the conversion. The conversion runs before the coded call, so an integer that does
    /// not fit its parameter reaches the evaluator as the arithmetic fault it codes, not as a
    /// failure of the function.
    /// </remarks>
    /// <param name="value">The argument as the row holds it.</param>
    /// <param name="target">The storage type the argument converts to; <see cref="DatabaseType.Null"/> for a pseudo-type parameter.</param>
    /// <param name="database">The database whose statement makes the call.</param>
    /// <param name="collation">The collation the call's input compares under.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    /// <returns>The result as the row holds it.</returns>
    /// <exception cref="OverflowException">An integer does not fit the parameter's type.</exception>
    /// <exception cref="DatabaseException">The function failed (<c>COHSQLE007</c>), or the database exception it threw.</exception>
    internal object? InvokeResolved(object? value, DatabaseType target, DatabaseName database, Collation collation,
        CancellationToken cancellationToken)
    {
        var argument = SqlValue.FromObject(value);
        if (target != DatabaseType.Null && target != argument.Type)
        {
            argument = SqlFunctionResolver.Coerce(argument, target, this, 0);
        }

        var arguments = new SqlArguments(new ReadOnlySpan<SqlValue>(in argument), database, collation, cancellationToken);
        SqlValue result;
        try
        {
            result = InvokeCore(in arguments);
        }
        catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
        {
            throw SqlEvaluationException.FunctionFailed(Name, exception);
        }

        return HasDeclaredResultType(result.Type, in arguments) ? result.ToObject() : CheckResultToObject(result, in arguments);
    }

    // The slow check's converted result lives in this frame, not in the frame every call makes.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private object? CheckResultToObject(in SqlValue result, scoped in SqlArguments arguments)
        => CheckResultSlow(result, in arguments).ToObject();

    /// <summary>Computes the function's value; the engine calls it through <see cref="Invoke"/>.</summary>
    /// <param name="arguments">
    /// The arguments: as many as the function declares, each of its parameter's type, and none NULL
    /// when the function is strict.
    /// </param>
    /// <returns>
    /// The result, of the declared type, or <see cref="SqlValue.Null"/>. A value of a type that widens
    /// to the declared one implicitly (INTEGER for BIGINT) is converted; any other type fails the
    /// statement as <c>COHSQLE007</c>, as a NULL does from a function that is
    /// <see cref="SqlFunction.IsNeverNull"/>.
    /// </returns>
    protected abstract SqlValue InvokeCore(scoped in SqlArguments arguments);

    /// <summary>
    /// Creates a scalar function of one argument from a delegate. The CLR types map to SQL types
    /// when this method runs (<see cref="long"/> to <see cref="SqlType.BigInt"/>, <see cref="string"/>
    /// to <see cref="SqlType.Text"/>, and so on for every storage type, a nullable value type
    /// mapping as its underlying type), so an unsupported type fails here, not at the first call.
    /// By default the function is strict: the delegate never receives a NULL argument, so a nullable
    /// parameter type changes nothing until <paramref name="nullBehavior"/> says
    /// <see cref="SqlNullBehavior.CalledOnNullInput"/>. A <see langword="null"/> result is SQL NULL.
    /// </summary>
    /// <remarks>
    /// An argument converts to its parameter's type the way overload resolution chose it: an exact
    /// type, or an implicit widening along INT8 → INT16 → INT32 → INT64 → NUMERIC → DOUBLE (REAL
    /// widens to DOUBLE), so a <see cref="long"/> parameter takes an INTEGER column. Nothing converts
    /// from text: a string literal does not reach a <see cref="DateOnly"/> or <see cref="Guid"/>
    /// parameter, which takes a column or parameter of its type. A function that needs the call's
    /// context (its collation or cancellation token) derives a leaf instead.
    /// </remarks>
    /// <typeparam name="T1">The argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <param name="nullBehavior">
    /// What a NULL argument does: strict by default, so the body never receives NULL; with
    /// <see cref="SqlNullBehavior.CalledOnNullInput"/> every parameter must be a reference or nullable
    /// type, and receives <see langword="null"/> for NULL.
    /// </param>
    /// <returns>The function, to register with <see cref="SqlFunctionCollection.Add"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank, or the function is called on NULL input and a parameter type cannot hold NULL.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, TResult>(string name, Func<T1, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput)
        => new SqlTypedScalarFunction<T1, TResult>(name, body, volatility, nullBehavior);

    /// <summary>Creates a scalar function of two arguments from a delegate; see <see cref="Create{T1, TResult}"/>.</summary>
    /// <typeparam name="T1">The first argument's CLR type.</typeparam>
    /// <typeparam name="T2">The second argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <param name="nullBehavior">
    /// What a NULL argument does: strict by default, so the body never receives NULL; with
    /// <see cref="SqlNullBehavior.CalledOnNullInput"/> every parameter must be a reference or nullable
    /// type, and receives <see langword="null"/> for NULL.
    /// </param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank, or the function is called on NULL input and a parameter type cannot hold NULL.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, T2, TResult>(string name, Func<T1, T2, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput)
        => new SqlTypedScalarFunction<T1, T2, TResult>(name, body, volatility, nullBehavior);

    /// <summary>Creates a scalar function of three arguments from a delegate; see <see cref="Create{T1, TResult}"/>.</summary>
    /// <typeparam name="T1">The first argument's CLR type.</typeparam>
    /// <typeparam name="T2">The second argument's CLR type.</typeparam>
    /// <typeparam name="T3">The third argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <param name="nullBehavior">
    /// What a NULL argument does: strict by default, so the body never receives NULL; with
    /// <see cref="SqlNullBehavior.CalledOnNullInput"/> every parameter must be a reference or nullable
    /// type, and receives <see langword="null"/> for NULL.
    /// </param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank, or the function is called on NULL input and a parameter type cannot hold NULL.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput)
        => new SqlTypedScalarFunction<T1, T2, T3, TResult>(name, body, volatility, nullBehavior);

    /// <summary>Creates a scalar function of four arguments from a delegate; see <see cref="Create{T1, TResult}"/>.</summary>
    /// <typeparam name="T1">The first argument's CLR type.</typeparam>
    /// <typeparam name="T2">The second argument's CLR type.</typeparam>
    /// <typeparam name="T3">The third argument's CLR type.</typeparam>
    /// <typeparam name="T4">The fourth argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="body">The function body; it must be thread-safe.</param>
    /// <param name="volatility">How stable the result is; <see cref="SqlFunctionVolatility.Volatile"/> by default.</param>
    /// <param name="nullBehavior">
    /// What a NULL argument does: strict by default, so the body never receives NULL; with
    /// <see cref="SqlNullBehavior.CalledOnNullInput"/> every parameter must be a reference or nullable
    /// type, and receives <see langword="null"/> for NULL.
    /// </param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank, or the function is called on NULL input and a parameter type cannot hold NULL.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    public static SqlScalarFunction Create<T1, T2, T3, T4, TResult>(string name, Func<T1, T2, T3, T4, TResult> body,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Volatile,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput)
        => new SqlTypedScalarFunction<T1, T2, T3, T4, TResult>(name, body, volatility, nullBehavior);
}
