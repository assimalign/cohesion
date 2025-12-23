using System;
using System.Runtime.ExceptionServices;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TResult"></typeparam>
public readonly struct Outcome<TResult> : IEither
{
    private readonly int _typeIndex;
    private readonly object? _value;

    Outcome(int typeIndex, object value) => (_typeIndex, _value) = (typeIndex, value);
    public Outcome(TResult value)
    {
        _value = value;
        _typeIndex = 1;
    }

    public Outcome(Exception value)
    {
        _value = ExceptionDispatchInfo.Capture(value);
        _typeIndex = 2;
    }


    int IEither.TypeIndex => _typeIndex;
    Type IEither.Type => _typeIndex switch
    {
        1 => typeof(TResult),
        2 => typeof(Exception),
        _ => throw new InvalidOperationException()
    };
    object? IEither.Value => _value;
    TResult AsT1 => (TResult)_value!;
    Exception AsT2 => ((ExceptionDispatchInfo)_value!).SourceException;

    public static implicit operator Outcome<TResult>(TResult value) => new Outcome<TResult>(value);
    public static implicit operator Outcome<TResult>(Exception value) => new Outcome<TResult>(value);
    public static explicit operator TResult(Outcome<TResult> either) => either.AsT1;
    public static explicit operator Exception(Outcome<TResult> either) => either.AsT2;


    public void Switch(Action<TResult> ifTResult, Action<Exception> ifException)
    {
        switch (_typeIndex)
        {
            case 1: ifTResult(AsT1); break;
            case 2: ifException(AsT2); break;
            default: throw new InvalidOperationException();
        }
    }

    public Outcome<TResult1> Match<TResult1>(Func<TResult, TResult1> ifT1) => _typeIndex switch
    {
        1 => ifT1(AsT1),
        2 => AsT2,
        _ => throw new InvalidOperationException()
    };

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

    public bool If(out TResult @if) => If(out @if, out _);
    public bool If(out TResult @if, out Exception @else)
    {
        switch (_typeIndex)
        {
            case 1:
                @if = AsT1;
                @else = default!;
                return true;
            case 2:
                @if = default!;
                @else = AsT2;
                return false;
            default:
                throw new InvalidOperationException();
        }
    }
    public bool If(out Exception @if) => If(out @if, out _);
    public bool If(out Exception @if, out TResult @else)
    {
        switch (_typeIndex)
        {
            case 1:
                @if = default!;
                @else = AsT1;
                return false;
            case 2:
                @if = AsT2;
                @else = default!;
                return true;
            default:
                throw new InvalidOperationException();
        }
    }
    public void ThrowIfException()
    {
        if (If(out Exception exception))
        {
            throw exception;
        }
    }

    public override string ToString() => $"{(this as IEither)?.Type?.Name}:{_value}";
}
