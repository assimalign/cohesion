using System;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair;

public abstract partial class KeyValueFunction
{
    /// <summary>Creates a scalar function from a statically compiled delegate.</summary>
    /// <typeparam name="TResult">The exact CLR primitive result type.</typeparam>
    /// <param name="name">The shared function identifier; default is refused.</param>
    /// <param name="body">The thread-safe scalar body.</param>
    /// <param name="nullBehavior">Whether the delegate receives NULL arguments.</param>
    /// <returns>The function to register in an engine builder.</returns>
    /// <exception cref="ArgumentNullException">The delegate is null.</exception>
    /// <exception cref="ArgumentException">The name is default, or a called-on-NULL parameter cannot hold NULL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The NULL policy is undefined.</exception>
    /// <exception cref="NotSupportedException">An argument or result CLR type is not a supported scalar primitive.</exception>
    public static KeyValueFunction Create<TResult>(KeyValueFunctionName name, Func<TResult> body,
        KeyValueNullBehavior nullBehavior = KeyValueNullBehavior.ReturnsNullOnNullInput)
        => new KeyValueTypedFunction<TResult>(name, body, nullBehavior);

    /// <summary>Creates a scalar function from a statically compiled delegate.</summary>
    /// <typeparam name="T1">The exact CLR primitive type of argument 1.</typeparam>
    /// <typeparam name="TResult">The exact CLR primitive result type.</typeparam>
    /// <param name="name">The shared function identifier; default is refused.</param>
    /// <param name="body">The thread-safe scalar body.</param>
    /// <param name="nullBehavior">Whether the delegate receives NULL arguments.</param>
    /// <returns>The function to register in an engine builder.</returns>
    /// <exception cref="ArgumentNullException">The delegate is null.</exception>
    /// <exception cref="ArgumentException">The name is default, or a called-on-NULL parameter cannot hold NULL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The NULL policy is undefined.</exception>
    /// <exception cref="NotSupportedException">An argument or result CLR type is not a supported scalar primitive.</exception>
    public static KeyValueFunction Create<T1, TResult>(KeyValueFunctionName name, Func<T1, TResult> body,
        KeyValueNullBehavior nullBehavior = KeyValueNullBehavior.ReturnsNullOnNullInput)
        => new KeyValueTypedFunction<T1, TResult>(name, body, nullBehavior);

    /// <summary>Creates a scalar function from a statically compiled delegate.</summary>
    /// <typeparam name="T1">The exact CLR primitive type of argument 1.</typeparam>
    /// <typeparam name="T2">The exact CLR primitive type of argument 2.</typeparam>
    /// <typeparam name="TResult">The exact CLR primitive result type.</typeparam>
    /// <param name="name">The shared function identifier; default is refused.</param>
    /// <param name="body">The thread-safe scalar body.</param>
    /// <param name="nullBehavior">Whether the delegate receives NULL arguments.</param>
    /// <returns>The function to register in an engine builder.</returns>
    /// <exception cref="ArgumentNullException">The delegate is null.</exception>
    /// <exception cref="ArgumentException">The name is default, or a called-on-NULL parameter cannot hold NULL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The NULL policy is undefined.</exception>
    /// <exception cref="NotSupportedException">An argument or result CLR type is not a supported scalar primitive.</exception>
    public static KeyValueFunction Create<T1, T2, TResult>(KeyValueFunctionName name, Func<T1, T2, TResult> body,
        KeyValueNullBehavior nullBehavior = KeyValueNullBehavior.ReturnsNullOnNullInput)
        => new KeyValueTypedFunction<T1, T2, TResult>(name, body, nullBehavior);

    /// <summary>Creates a scalar function from a statically compiled delegate.</summary>
    /// <typeparam name="T1">The exact CLR primitive type of argument 1.</typeparam>
    /// <typeparam name="T2">The exact CLR primitive type of argument 2.</typeparam>
    /// <typeparam name="T3">The exact CLR primitive type of argument 3.</typeparam>
    /// <typeparam name="TResult">The exact CLR primitive result type.</typeparam>
    /// <param name="name">The shared function identifier; default is refused.</param>
    /// <param name="body">The thread-safe scalar body.</param>
    /// <param name="nullBehavior">Whether the delegate receives NULL arguments.</param>
    /// <returns>The function to register in an engine builder.</returns>
    /// <exception cref="ArgumentNullException">The delegate is null.</exception>
    /// <exception cref="ArgumentException">The name is default, or a called-on-NULL parameter cannot hold NULL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The NULL policy is undefined.</exception>
    /// <exception cref="NotSupportedException">An argument or result CLR type is not a supported scalar primitive.</exception>
    public static KeyValueFunction Create<T1, T2, T3, TResult>(KeyValueFunctionName name, Func<T1, T2, T3, TResult> body,
        KeyValueNullBehavior nullBehavior = KeyValueNullBehavior.ReturnsNullOnNullInput)
        => new KeyValueTypedFunction<T1, T2, T3, TResult>(name, body, nullBehavior);

    /// <summary>Creates a scalar function from a statically compiled delegate.</summary>
    /// <typeparam name="T1">The exact CLR primitive type of argument 1.</typeparam>
    /// <typeparam name="T2">The exact CLR primitive type of argument 2.</typeparam>
    /// <typeparam name="T3">The exact CLR primitive type of argument 3.</typeparam>
    /// <typeparam name="T4">The exact CLR primitive type of argument 4.</typeparam>
    /// <typeparam name="TResult">The exact CLR primitive result type.</typeparam>
    /// <param name="name">The shared function identifier; default is refused.</param>
    /// <param name="body">The thread-safe scalar body.</param>
    /// <param name="nullBehavior">Whether the delegate receives NULL arguments.</param>
    /// <returns>The function to register in an engine builder.</returns>
    /// <exception cref="ArgumentNullException">The delegate is null.</exception>
    /// <exception cref="ArgumentException">The name is default, or a called-on-NULL parameter cannot hold NULL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The NULL policy is undefined.</exception>
    /// <exception cref="NotSupportedException">An argument or result CLR type is not a supported scalar primitive.</exception>
    public static KeyValueFunction Create<T1, T2, T3, T4, TResult>(KeyValueFunctionName name, Func<T1, T2, T3, T4, TResult> body,
        KeyValueNullBehavior nullBehavior = KeyValueNullBehavior.ReturnsNullOnNullInput)
        => new KeyValueTypedFunction<T1, T2, T3, T4, TResult>(name, body, nullBehavior);

}
