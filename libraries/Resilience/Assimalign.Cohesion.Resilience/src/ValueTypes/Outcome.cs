using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("Outcome: {ToString()}")]
[DebuggerTypeProxy(typeof(DebuggerView))]
public readonly struct Outcome : IEither
{
    private readonly int _typeIndex = -1;
    private readonly object? _typeValue;

    Outcome(bool isSuccess)
    {
        _typeIndex = 1;
        _typeValue = isSuccess;
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
        1 => typeof(bool),
        2 => AsT2!.GetType(),
        _ => throw new InvalidOperationException()
    };
    object? IEither.Value => _typeValue;
    bool AsT1 => (bool)_typeValue!;
    Exception AsT2 => ((ExceptionDispatchInfo)_typeValue!).SourceException;

    /// <summary>
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    public bool IsSuccess()
    {
        return _typeIndex == 1;
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
    public T Match<T>(Func<T> onSuccess, Func<Exception, T> onFailure)
    {
        return _typeIndex switch
        {
            1 => onSuccess.Invoke(),
            2 => onFailure.Invoke(AsT2),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="onSuccess"></param>
    /// <param name="onFailure"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void Switch(Action onSuccess, Action<Exception> onFailure)
    {
        switch (_typeIndex)
        {
            case 1: onSuccess(); break;
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

        return "Outcome: null";
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public static Outcome Success { get; } = new Outcome(true);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public static Outcome Failure(Exception exception) => exception;


    /// <summary>
    /// Implicitly converts a ResilienceException instance to an Outcome.
    /// </summary>
    /// <remarks>
    /// This conversion enables seamless handling of ResilienceException objects within APIs or
    /// methods that expect an Outcome, simplifying error propagation and response management.
    /// </remarks>
    /// <param name="value">The ResilienceException instance to convert.</param>
    public static implicit operator Outcome(Exception value)
    {
        return new Outcome(value);
    }

    /// <summary>
    /// Converts the specified Outcome instance to an Exception by extracting the encapsulated exception value.
    /// </summary>
    /// <remarks>
    /// Use this explicit conversion operator to retrieve the underlying Exception from an Outcome
    /// that represents a failure. If the Outcome does not contain an Exception, accessing this operator may result in
    /// an invalid operation.
    /// </remarks>
    /// <param name="either">The Outcome instance to convert. This instance must contain an Exception in its AsT2 property.</param>
    public static explicit operator Exception(Outcome either)
    {
        return either.AsT2;
    }


    partial class DebuggerView
    {
        public DebuggerView(Outcome outcome)
        {
            if (outcome.IsFailure(out Exception? exception))
            {
                Exception = exception;
            }
        }
        public bool IsSuccess => Exception is null;
        public Exception? Exception { get; set; }
    }
}
