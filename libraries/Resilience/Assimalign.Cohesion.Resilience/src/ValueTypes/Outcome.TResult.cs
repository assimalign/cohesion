using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// A sum type representing a either a <typeparamref name="TResult"/> or <see cref="=Exception"/>.
/// </summary>
/// <typeparam name="TResult"></typeparam>
[DebuggerDisplay("{ToString()}")]
[DebuggerTypeProxy(typeof(Outcome<>.DebuggerView))]
public readonly struct Outcome<TResult> : IEither
{
    private readonly int _typeIndex = -1;
    private readonly object? _typeValue;

    public Outcome(TResult value)
    {
        _typeIndex = 1;
        _typeValue = value;
    }

    public Outcome(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _typeIndex = 2;
        _typeValue = ExceptionDispatchInfo.Capture(exception);
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
    public bool IsSuccess([NotNullWhen(true)] out TResult? result)
    {
        result = default!;
        if (_typeIndex == 1)
        {
            result = AsT1!;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public bool IsFailure()
    {
        return _typeIndex == 2;
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
        }

        return exception is not null;
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

        if (_typeIndex == 2 && AsT2 is TException exception1)
        {
            exception = exception1;
        }

        return exception is not null;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="onSuccess"></param>
    /// <param name="onFailure"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public T Match<T>(Func<TResult, T> onSuccess, Func<Exception, T> onFailure)
    {
        return _typeIndex switch
        {
            1 => onSuccess.Invoke(AsT1),
            2 => onFailure.Invoke(AsT2),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TResult1"></typeparam>
    /// <param name="onSuccess"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public Outcome<TResult1> Match<TResult1>(Func<TResult, TResult1> onSuccess)
    {
        return _typeIndex switch
        {
            1 => onSuccess(AsT1),
            2 => AsT2,
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="onSuccess"></param>
    /// <param name="onFailure"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void Switch(Action<TResult> onSuccess, Action<Exception> onFailure)
    {
        switch (_typeIndex)
        {
            case 1: onSuccess(AsT1); break;
            case 2: onFailure(AsT2); break;
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
        if (IsFailure())
        {
            return "Failure: " + _typeValue!.GetType().Name;
        }

        if (IsSuccess())
        {
            return "Success: " + _typeValue!.GetType().Name;
        }

        return "Outcome: none";
    }

    public static implicit operator Outcome<TResult>(TResult value)
    {
        return new Outcome<TResult>(value);
    }

    public static implicit operator Outcome<TResult>(Exception value)
    {
        return new Outcome<TResult>(value);
    }

    public static explicit operator TResult(Outcome<TResult> either)
    {
        return either.AsT1;
    }

    public static explicit operator Exception(Outcome<TResult> either)
    {
        return either.AsT2;
    }



    partial class DebuggerView
    {
        public DebuggerView(Outcome<TResult> outcome)
        {
            if (outcome.IsFailure(out Exception? exception))
            {
                Exception = exception;
            }

            if (outcome.IsSuccess(out TResult? result))
            {
                Result = result;
            }
        }

        public bool IsSuccess => Exception is null;
        public TResult? Result { get; }
        public Exception? Exception { get; }
    }
}
