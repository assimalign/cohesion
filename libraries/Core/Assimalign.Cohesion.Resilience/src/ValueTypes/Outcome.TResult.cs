using System;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// A sum type representing a either a <typeparamref name="TResult"/> or <see cref="=Exception"/>.
/// </summary>
/// <typeparam name="TResult"></typeparam>
public readonly struct Outcome<TResult> : IEither
{
    private readonly int _typeIndex;
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
    /// Gets a value indicating whether the operation completed successfully.
    /// </summary>
    public bool IsSuccess => _typeIndex == 1;

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
        if (!IsSuccess && _typeIndex == 2)
        {
            throw ((Exception)this);
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

    

    //public ValueEither<T1, TResult2> Match<TResult2>(Func<T2, TResult2> ifT2) => _typeIndex switch
    //{
    //    1 => AsT1,
    //    2 => ifT2(AsT2),
    //    _ => throw new InvalidOperationException()
    //};


    //public ValueEither<TResult1, T2> Match<TResult1>(Func<T1, ValueEither<TResult1, T2>> ifT1) => _typeIndex switch
    //{
    //    1 => ifT1(AsT1),
    //    2 => AsT2,
    //    _ => throw new InvalidOperationException()
    //};
    //public ValueEither<T1, TResult2> Match<TResult2>(Func<T2, ValueEither<T1, TResult2>> ifT2) => _typeIndex switch
    //{
    //    1 => AsT1,
    //    2 => ifT2(AsT2),
    //    _ => throw new InvalidOperationException()
    //};


    //public T2 Match
    //    (Func<T1, T2> ifT1) => _typeIndex switch
    //    {
    //        1 => ifT1(AsT1),
    //        2 => AsT2,
    //        _ => throw new InvalidOperationException()
    //    };
    //public ValueEither<T1, T2> Match
    //    (Func<T1, T2> ifT1, Func<T1, bool> when) => _typeIndex switch
    //    {
    //        1 when (when(AsT1)) => ifT1(AsT1),
    //        2 => AsT2,
    //        1 => AsT1,
    //        _ => throw new InvalidOperationException()
    //    };
    //public T1 Match
    //    (Func<T2, T1> ifT2) => _typeIndex switch
    //    {
    //        1 => AsT1,
    //        2 => ifT2(AsT2),
    //        _ => throw new InvalidOperationException()
    //    };
    //public ValueEither<T1, T2> Match
    //    (Func<T2, T1> ifT2, Func<T2, bool> when) => _typeIndex switch
    //    {
    //        1 => AsT1,
    //        2 when (when(AsT2)) => ifT2(AsT2),
    //        2 => AsT2,
    //        _ => throw new InvalidOperationException()
    //    };

    //public T2 ThrowIf
    //    (Func<T1, Exception> ifT1) => _typeIndex switch
    //    {
    //        1 => throw ifT1(AsT1),
    //        2 => AsT2,
    //        _ => throw new InvalidOperationException()
    //    };
    //public ValueEither<T1, T2> ThrowIf
    //    (Func<T1, Exception> ifT1, Func<T1, bool> when) => _typeIndex switch
    //    {
    //        1 when (when(AsT1)) => throw ifT1(AsT1),
    //        2 => AsT2,
    //        1 => AsT1,
    //        _ => throw new InvalidOperationException()
    //    };
    //public T1 ThrowIf
    //    (Func<T2, Exception> ifT2) => _typeIndex switch
    //    {
    //        1 => AsT1,
    //        2 => throw ifT2(AsT2),
    //        _ => throw new InvalidOperationException()
    //    };
    //public ValueEither<T1, T2> ThrowIf
    //    (Func<T2, Exception> ifT2, Func<T2, bool> when) => _typeIndex switch
    //    {
    //        1 => AsT1,
    //        2 when (when(AsT2)) => throw ifT2(AsT2),
    //        2 => AsT2,
    //        _ => throw new InvalidOperationException()
    //    };


    
    
}
