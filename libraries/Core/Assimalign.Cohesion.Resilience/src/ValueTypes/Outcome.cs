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
    private readonly int _typeIndex;
    private readonly object? _typeValue;

    public Outcome(bool isSuccess)
    {
        _typeValue = isSuccess;
        _typeIndex = 1;
    }

    public Outcome(Exception exception)
    {
        _typeValue = ExceptionDispatchInfo.Capture(exception);
        _typeIndex = 2;
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
    /// 
    /// </summary>
    /// <returns></returns>
    public static Outcome Success() => true;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public static Outcome Failure(Exception exception) => exception; 

    /// <summary>
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    public bool IsSuccess() => _typeIndex == 1;

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

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="match1"></param>
    /// <param name="match2"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public T Match<T>(Func<bool, T> match1, Func<Exception, T> match2) => _typeIndex switch
    {
        1 => match1.Invoke(AsT1),
        2 => match2.Invoke(AsT2),
        _ => throw new InvalidOperationException()
    };

    /// <summary>
    /// 
    /// </summary>
    /// <param name="ifTResult"></param>
    /// <param name="ifException"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void Switch(Action<bool> ifTResult, Action<Exception> ifException)
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
        if (IsFailure(out Exception? exception))
        {
            throw exception;
        }
    }


    public override string ToString()
    {
        if (IsFailure(out Exception? exception))
        {
            return "Failure - " + exception.ToString();
        }

        return "Success";
    }


    public static implicit operator Outcome(bool value) => new Outcome(value);
    public static implicit operator Outcome(Exception value) => new Outcome(value);
    public static explicit operator bool(Outcome either) => either.AsT1;
    public static explicit operator Exception(Outcome either) => either.AsT2;




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
