using System;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
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
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    public bool IsSuccess => _typeIndex == 1;

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public bool Is<T>() => _typeValue is T;

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
        if (!IsSuccess && _typeIndex == 2)
        {
            throw ((Exception)this);
        }
    }


    public static implicit operator Outcome(bool value) => new Outcome(value);
    public static implicit operator Outcome(Exception value) => new Outcome(value);
    public static explicit operator bool(Outcome either) => either.AsT1;
    public static explicit operator Exception(Outcome either) => either.AsT2;
}
