using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// A sum type representing a either a <typeparamref name="TResult"/> or <see cref="=Exception"/>.
/// </summary>
/// <typeparam name="TResult"></typeparam>
[DebuggerDisplay("Outcome: {ToString()}")]
//[DebuggerTypeProxy(typeof(DebuggerView))]
public readonly struct Outcome<TResult> : IEither
{
    private readonly int _typeIndex = -1;
    private readonly object? _typeValue;

    public Outcome(TResult value)
    {
        _typeValue = value;
        _typeIndex = 1;
    }

    public Outcome(Exception value)
    {
        _typeValue = ExceptionDispatchInfo.Capture(value);
        _typeIndex = 2;
    }


    int IEither.TypeIndex => _typeIndex;
    Type IEither.Type => _typeIndex switch
    {
        1 => typeof(TResult),
        2 => AsT2!.GetType(),
        _ => throw new InvalidOperationException()
    };
    object? IEither.Value => _typeValue;
    TResult AsT1 => (TResult)_typeValue!;
    Exception AsT2 => ((ExceptionDispatchInfo)_typeValue!).SourceException;

    /// <summary>
    /// Determines whether the current result represents a successful outcome.
    /// </summary>
    /// <returns>true if the result is successful; otherwise, false.</returns>
    public bool IsSuccess()
    {
        return _typeIndex == 1;
    }

    /// <summary>
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    public bool IsSuccess([NotNullWhen(true)] out TResult result)
    {
        result = default!;
        if (_typeIndex == 2)
        {
            result = AsT1!;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public bool IsFailure([NotNullWhen(true)] out Exception? exception)
    {
        exception = null;
        if (_typeIndex == 2)
        {
            exception = AsT2;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TException"></typeparam>
    /// <param name="exception"></param>
    /// <returns></returns>
    public bool IsFailure<TException>([NotNullWhen(true)] out TException? exception) where TException : Exception
    {
        exception = null;
        if (_typeIndex == 2 && AsT2 is TException ex)
        {
            exception = ex;
            return true;
        }
        return false;
    }

    public bool IsFailure([NotNullWhen(true)] out ExceptionDispatchInfo? dispatchInfo)
    {
        dispatchInfo = null;

        if (_typeIndex == 2)
        {
            dispatchInfo = ((ExceptionDispatchInfo)_typeValue!);
        }

        return dispatchInfo is not null;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="match1"></param>
    /// <param name="match2"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public T Match<T>(Func<TResult, T> match1, Func<Exception, T> match2) => _typeIndex switch
    {
        1 => match1.Invoke(AsT1),
        2 => match2.Invoke(AsT2),
        _ => throw new InvalidOperationException()
    };

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TResult1"></typeparam>
    /// <param name="ifT1"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public Outcome<TResult1> Match<TResult1>(Func<TResult, TResult1> ifT1) => _typeIndex switch
    {
        1 => ifT1(AsT1),
        2 => AsT2,
        _ => throw new InvalidOperationException()
    };

    /// <summary>
    /// 
    /// </summary>
    /// <param name="ifTResult"></param>
    /// <param name="ifException"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void Switch(Action<TResult> ifTResult, Action<Exception> ifException)
    {
        switch (_typeIndex)
        {
            case 1: ifTResult(AsT1); break;
            case 2: ifException(AsT2); break;
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public void ThrowIfException()
    {
        if (_typeIndex == 2)
        {
            ((ExceptionDispatchInfo)_typeValue!).Throw();
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{(this as IEither)?.Type?.Name}:{_typeValue}";
    }

    public static implicit operator Outcome<TResult>(TResult value) => new Outcome<TResult>(value);
    public static implicit operator Outcome<TResult>(Exception value) => new Outcome<TResult>(value);
    public static explicit operator TResult(Outcome<TResult> either) => either.AsT1;
    public static explicit operator Exception(Outcome<TResult> either) => either.AsT2;
}
